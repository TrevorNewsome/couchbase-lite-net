﻿//
//  Blob.cs
//
//  Author:
//  	Jim Borden  <jim.borden@couchbase.com>
//
//  Copyright (c) 2017 Couchbase, Inc All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

using Couchbase.Lite.Logging;
using Couchbase.Lite.Support;
using Couchbase.Lite.Util;
using LiteCore;
using LiteCore.Interop;

namespace Couchbase.Lite.DB
{
    internal sealed unsafe class Blob : ThreadSafe, IBlob, IJsonMapped
    {
        #region Constants

        private const string BlobType = "blob";
        private const uint MaxCachedContentLength = 8 * 1024;
        private const int ReadBufferSize = 8 * 1024;
        private const string Tag = nameof(Blob);
        private const string TypeMetaProperty = "_cbltype";

        #endregion

        #region Variables

        private readonly Dictionary<string, object> _properties;
        private byte[] _content;
        private Database _db;
        private Stream _initialContentStream;

        #endregion

        #region Properties

        public byte[] Content
        {
            get {
                if(_content != null) {
                    return _content;
                }

                if(_db != null) {
                    C4BlobStore* blobStore;
                    C4BlobKey key;
                    if(!GetBlobStore(&blobStore, &key)) {
                        return null;
                    }

                    //TODO: If data is large, can get the file path & memory-map it
                    var content = Native.c4blob_getContents(blobStore, key, null);
                    if(content?.Length <= MaxCachedContentLength) {
                        _content = content;
                    }

                    return content;
                } else {
                    if(_initialContentStream == null) {
                        throw new InvalidOperationException("Blob has no data available");
                    }

                    var result = new List<byte>();
                    using(var reader = new BinaryReader(_initialContentStream)) {
                        byte[] buffer;
                        do {
                            buffer = reader.ReadBytes(ReadBufferSize);
                            result.AddRange(buffer);
                        } while(buffer.Length == ReadBufferSize);
                    }

                    _initialContentStream.Dispose();
                    _initialContentStream = null;
                    _content = result.ToArray();
                    Length = (ulong)_content.Length;
                    return _content;
                }
            }
        }

        public Stream ContentStream
        {
            get {
                if(_db != null) {
                    C4BlobStore* blobStore;
                    C4BlobKey key;
                    if(!GetBlobStore(&blobStore, &key)) {
                        return null;
                    }

                    return new BlobReadStream(blobStore, key);
                } else {
                    return _content != null ? new MemoryStream(_content) : null;
                }
            }
        }

        public string ContentType { get; }

        public string Digest { get; private set; }

        public IReadOnlyDictionary<string, object> JsonRepresentation
        {
            get {
                if(_db == null) {
                    throw new InvalidOperationException("Blob hasn't been saved in the database yet");
                }

                var json = new Dictionary<string, object>(MutableProperties) {
                    [TypeMetaProperty] = BlobType
                };

                return json;
            }
        }

        public ulong Length { get; private set; }

        public IReadOnlyDictionary<string, object> Properties
        {
            get {
                return new ReadOnlyDictionary<string, object>(MutableProperties);
            }
        }

        private IDictionary<string, object> MutableProperties
        {
            get {
                if(_properties != null) {
                    return _properties;
                }

                return new NonNullDictionary<string, object> {
                    ["digest"] = Digest,
                    ["length"] = Length > 0 ? (object)Length : null,
                    ["content-type"] = ContentType
                };
            }
        }

        #endregion

        #region Constructors

        public Blob(string contentType, byte[] content)
        {
            if(content == null) {
                throw new ArgumentNullException(nameof(content));
            }

            ContentType = contentType;
            _content = content;
            Length = (ulong)content.Length;
        }

        public Blob(string contentType, Stream stream)
        {
            if(stream == null) {
                throw new ArgumentNullException(nameof(stream));
            }

            ContentType = contentType;
            _initialContentStream = stream;
        }

        public Blob(string contentType, Uri fileUrl)
        {
            if(fileUrl == null) {
                throw new ArgumentNullException(nameof(fileUrl));
            }

            if(!fileUrl.IsFile) {
                throw new ArgumentException($"{fileUrl} must be a file-based URL", nameof(fileUrl));
            }

            ContentType = contentType;
            _initialContentStream = File.OpenRead(fileUrl.AbsolutePath);
        }

        internal Blob(Database db, IDictionary<string, object> properties)
        {
            if(db == null) {
                throw new ArgumentNullException(nameof(db));
            }

            if(properties == null) {
                throw new ArgumentNullException(nameof(properties));
            }

            _db = db;
            _properties = new Dictionary<string, object>(properties) {
                [TypeMetaProperty] = null
            };

            Length = properties.GetCast<ulong>("length");
            Digest = properties.GetCast<string>("digest");
            if(Digest == null) {
                Log.To.Database.W(Tag, "Blob read from database has missing digest");
            }
        }

        #endregion

        #region Internal Methods

        internal void Install(Database db)
        {
            if(db == null) {
                throw new ArgumentNullException(nameof(db));
            }

            if(_db != null) {
                if(db != _db) {
                    throw new InvalidOperationException("Blob belongs to a different database");
                }

                return;
            }

            var store = db.BlobStore;
            var key = default(C4BlobKey);
            if(_content != null) {
                LiteCoreBridge.Check(err => {
                    var tmpKey = default(C4BlobKey);
                    var s = Native.c4blob_create(store, _content, &tmpKey, err);
                    key = tmpKey;
                    return s;
                });
            } else {
                if(_initialContentStream == null) {
                    throw new InvalidOperationException("No data available to write for install");
                }

                Length = 0;
                var contentStream = _initialContentStream;
                using(var blobOut = new BlobWriteStream(store)) {
                    contentStream.CopyTo(blobOut, ReadBufferSize);
                    blobOut.Flush();
                    key = blobOut.Key;
                }

                _initialContentStream.Dispose();
                _initialContentStream = null;
            }

            Digest = Native.c4blob_keyToString(key);
            _db = db;
        }

        #endregion

        #region Private Methods

        private bool GetBlobStore(C4BlobStore** outBlobStore, C4BlobKey* outKey)
        {
            try {
                *outBlobStore = _db.BlobStore;
                return Digest != null && Native.c4blob_keyFromString(Digest, outKey);
            } catch(InvalidOperationException) {
                return false;
            }
        }

        #endregion

        #region Overrides

        public override string ToString()
        {
            return $"Blob[{ContentType}; {(Length + 512) / 1024} KB]";
        }

        #endregion

        #region IJsonMapped

        public void WriteTo(IJsonWriter writer)
        {
            writer.Write("digest", Digest);
            writer.Write("length", Length);
            writer.Write("content-type", ContentType);
            writer.Write(TypeMetaProperty, BlobType);
        }

        #endregion
    }
}