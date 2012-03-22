using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers;
using Lucene.Net.Search;
using Lucene.Net.Store;
using RavenFS.Extensions;
using RavenFS.Infrastructure;
using Version = Lucene.Net.Util.Version;
using System.Linq;

namespace RavenFS.Search
{
	public class IndexStorage : IDisposable
	{
		private readonly string path;
		private FSDirectory directory;
		private LowerCaseKeywordAnalyzer analyzer;
		private IndexWriter writer;
		private readonly object writerLock = new object();
		private IndexSearcher searcher;

		public IndexStorage(string path, NameValueCollection _)
		{
			this.path = Path.Combine(path.ToFullPath(), "Index.ravenfs");
		}

		public void Initialize()
		{
			directory = FSDirectory.Open(new DirectoryInfo(path));
			if (IndexWriter.IsLocked(directory))
				IndexWriter.Unlock(directory);

			analyzer = new LowerCaseKeywordAnalyzer();
			writer = new IndexWriter(directory, analyzer, IndexWriter.MaxFieldLength.UNLIMITED);
			writer.SetMergeScheduler(new ErrorLoggingConcurrentMergeScheduler());
			searcher = new IndexSearcher(writer.GetReader());
		}

		public string[] Query(string query, string[] sortFields, int start, int pageSize, out int totalResults)
		{
			var queryParser = new QueryParser(Version.LUCENE_29, "", analyzer);
			var q = queryParser.Parse(query);

			var topDocs = ExecuteQuery(sortFields, q, pageSize + start);

			var results = new List<string>();

			for (var i = start; i < pageSize + start && i < topDocs.totalHits; i++)
			{
				var document = searcher.Doc(topDocs.scoreDocs[i].doc);
				results.Add(document.Get("__key"));
			}
			totalResults = topDocs.totalHits;
			return results.ToArray();
		}

	    private TopDocs ExecuteQuery(string[] sortFields, Query q, int size)
		{
			TopDocs topDocs;
			if (sortFields != null && sortFields.Length > 0)
			{
				var sort = new Sort(sortFields.Select(field =>
				{
					var desc = field.StartsWith("-");
					if (desc)
						field = field.Substring(1);
					return new SortField(field, SortField.STRING, desc);
				}).ToArray());
				topDocs = searcher.Search(q, null, size, sort);
			}
			else
			{
				topDocs = searcher.Search(q, null, size);
			}
			return topDocs;
		}

		public void Index(string key, NameValueCollection metadata)
		{
			lock (writerLock)
			{
				var lowerKey = key.ToLowerInvariant();
				var doc = CreateDocument(lowerKey, metadata);

				foreach (var metadataKey in metadata.AllKeys)
				{
					var values = metadata.GetValues(metadataKey);
					if (values == null)
						continue;

					foreach (var value in values)
					{
						doc.Add(new Field(metadataKey, value, Field.Store.NO, Field.Index.ANALYZED_NO_NORMS));
					}
				}

				writer.DeleteDocuments(new Term("__key", lowerKey));
				writer.AddDocument(doc);
				// yes, this is slow, but we aren't expecting high writes count
				writer.Commit();
				ReplaceSearcher();
			}
		}

		private static Document CreateDocument(string lowerKey, NameValueCollection metadata)
		{
			var doc = new Document();
			doc.Add(new Field("__key", lowerKey, Field.Store.YES, Field.Index.NOT_ANALYZED_NO_NORMS));
            // the reversed version of the key is used to allow searches that start with wildcards
			doc.Add(new Field("__rkey", lowerKey.Reverse(), Field.Store.YES, Field.Index.NOT_ANALYZED_NO_NORMS));
			int level = 0;
			var directoryName = Path.GetDirectoryName(lowerKey);
			do
			{
				level += 1;
				directoryName = (string.IsNullOrEmpty(directoryName) ? "" : directoryName.Replace("\\", "/"));
				if (directoryName.StartsWith("/") == false)
					directoryName = "/" + directoryName;
				doc.Add(new Field("__directory", directoryName, Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));
				directoryName = Path.GetDirectoryName(directoryName);
			} while (directoryName != null);
			doc.Add(new Field("__modified", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Field.Store.NO,
							  Field.Index.NOT_ANALYZED_NO_NORMS));
			doc.Add(new Field("__level", level.ToString(), Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));
			long len;
			if (long.TryParse(metadata["Content-Length"], out len))
			{
				doc.Add(new Field("__size", len.ToString("D20"), Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));
			}

			return doc;
		}

		public void Dispose()
		{
			analyzer.Close();
			searcher.GetIndexReader().Close();
			searcher.Close();
			writer.Close();
			directory.Close();
		}

		public void Delete(string key)
		{
		    var lowerKey = key.ToLowerInvariant();

			lock (writerLock)
			{
                writer.DeleteDocuments(new Term("__key", lowerKey));
				writer.Optimize();
				writer.Commit();
				ReplaceSearcher();
			}
		}

		private void ReplaceSearcher()
		{
			var currentSearcher = searcher;
			currentSearcher.GetIndexReader().Close();
			currentSearcher.Close();

			searcher = new IndexSearcher(writer.GetReader());
		}

		public IEnumerable<string> GetTermsFor(string field, string fromValue)
		{
			var termEnum = searcher.GetIndexReader().Terms(new Term(field, fromValue ?? string.Empty));
			try
			{
				if (string.IsNullOrEmpty(fromValue) == false) // need to skip this value
				{
					while (termEnum.Term() == null || fromValue.Equals(termEnum.Term().Text()))
					{
						if (termEnum.Next() == false)
							yield break;
					}
				}
				while (termEnum.Term() == null ||
					field.Equals(termEnum.Term().Field()))
				{
					if (termEnum.Term() != null)
					{
						var item = termEnum.Term().Text();
							yield return item;
					}

					if (termEnum.Next() == false)
						break;
				}
			}
			finally
			{
				termEnum.Close();
			}
		}
	}
}