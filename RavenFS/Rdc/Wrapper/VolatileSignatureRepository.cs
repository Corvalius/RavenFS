﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using RavenFS.Extensions;

namespace RavenFS.Rdc.Wrapper
{
    public class VolatileSignatureRepository : ISignatureRepository
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();
        private IList<Stream> _tochedStreams;

        private readonly string baseDirectory;

        public VolatileSignatureRepository(string path)
        {
        	baseDirectory = path.ToFullPath();
            Directory.CreateDirectory(baseDirectory);
        }

        public Stream GetContentForReading(string sigName)
        {
            return File.OpenRead(NameToPath(sigName));
        }

        public Stream CreateContent(string sigName)
        {
            var file = File.Create(NameToPath(sigName), 1024 * 128, FileOptions.Asynchronous);
            log.Info("File {0} created", sigName);
            return file;
        }

        public void Flush(IEnumerable<SignatureInfo> signatureInfos)
        {
            // nothing to do
        }

        public IEnumerable<SignatureInfo> GetByFileName(string fileName)
        {
            return from item in GetSigFileNamesByFileName(fileName)
                   select SignatureInfo.Parse(item);
        }

        public void Clean(string fileName)
        {
            foreach (var item in GetSigFileNamesByFileName(fileName))
            {
                File.Delete(item);
                log.Info("File {0} removed", item);
            }
        }

        public DateTime? GetLastUpdate(string fileName)
        {
            var preResult = from item in GetSigFileNamesByFileName(fileName)
                            let lastWriteTime = new FileInfo(item).LastWriteTime
                            orderby lastWriteTime descending
                            select lastWriteTime;
            if (preResult.Count() > 0)
            {
                return preResult.First();
            }
            return null;
        }

        private IEnumerable<string> GetSigFileNamesByFileName(string fileName)
        {
            return Directory.GetFiles(baseDirectory, fileName + "*.sig");
        }

        private string NameToPath(string name)
        {
            return Path.GetFullPath(Path.Combine(baseDirectory, name));
        }

        public void Dispose()
        {
            Directory.Delete(baseDirectory, true);
        }
    }
}
