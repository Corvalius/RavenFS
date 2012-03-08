using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using RavenFS.Extensions;
using RavenFS.Infrastructure;
using RavenFS.Storage;
using System.Linq;
using RavenFS.Util;

namespace RavenFS.Controllers
{
	public class FilesController : RavenController
	{
		public List<FileHeader> Get()
		{
			List<FileHeader> fileHeaders = null;
			Storage.Batch(accessor =>
			{
				fileHeaders = accessor.ReadFiles(Paging.Start, Paging.PageSize).ToList();
			});
			return fileHeaders;
		}

		public HttpResponseMessage Get(string filename)
		{
			filename = Uri.UnescapeDataString(filename);
			FileAndPages fileAndPages = null;
			try
			{
				Storage.Batch(accessor => fileAndPages = accessor.GetFile(filename, 0, 0));
			}
			catch (FileNotFoundException)
			{
				throw new HttpResponseException(HttpStatusCode.NotFound);
			}

			var ravenReadOnlyStream = new RavenReadOnlyStream(Storage, BufferPool, filename);
			var result = StreamResult(filename, ravenReadOnlyStream);
			MetadataExtensions.AddHeaders(result, fileAndPages);
			return result;
		}

		public HttpResponseMessage Delete(string filename)
		{
			filename = Uri.UnescapeDataString(filename); 
			Search.Delete(filename);
			Storage.Batch(accessor => accessor.Delete(filename));

			return new HttpResponseMessage(HttpStatusCode.NoContent);
		}

		[AcceptVerbs("HEAD")]
		public HttpResponseMessage Head(string filename)
		{
			filename = Uri.UnescapeDataString(filename); 
			FileAndPages fileAndPages = null;
			try
			{
				Storage.Batch(accessor => fileAndPages = accessor.GetFile(filename, 0, 0));
			}
			catch (FileNotFoundException)
			{
				return new HttpResponseMessage(HttpStatusCode.NotFound);
			}

			var httpResponseMessage = new HttpResponseMessage(HttpStatusCode.OK);
			httpResponseMessage.Content = new HttpMessageContent(httpResponseMessage);
			MetadataExtensions.AddHeaders(httpResponseMessage, fileAndPages);
			return httpResponseMessage;
		}

		public HttpResponseMessage Post(string filename)
		{
			filename = Uri.UnescapeDataString(filename); 
			var headers = Request.Headers.FilterHeaders();
			headers.UpdateLastModified();
			try
			{
				Storage.Batch(accessor => accessor.UpdateFileMetadata(filename, headers));
				Search.Index(filename, headers);
			}
			catch (FileNotFoundException)
			{
				return new HttpResponseMessage(HttpStatusCode.NotFound);
			}

			return new HttpResponseMessage(HttpStatusCode.NoContent);
		}

		public Task Put(string filename)
		{
			filename = Uri.UnescapeDataString(filename);
			Storage.Batch(accessor =>
			{
				accessor.Delete(filename);

				var headers = Request.Headers.FilterHeaders();
				headers.UpdateLastModified();
				
				long? contentLength = Request.Content.Headers.ContentLength;
				if (Request.Headers.TransferEncodingChunked ?? false)
				{
					contentLength = null;
				}
				accessor.PutFile(filename, contentLength, headers);

				Search.Index(filename, headers);
			});

			return Request.Content.ReadAsStreamAsync()
				.ContinueWith(task =>
				{
					var readFileToDatabase = new ReadFileToDatabase(BufferPool, Storage, task.Result, filename);
					return readFileToDatabase.Execute()
						.ContinueWith(readingTask =>
						{
							readFileToDatabase.Dispose();
							return readingTask;
						})
						.Unwrap();
				})
				.Unwrap();
		}

		private class ReadFileToDatabase : IDisposable
		{
			private readonly BufferPool bufferPool;
			private readonly TransactionalStorage storage;
			private readonly string filename;
			private int pos;
			readonly byte[] buffer;
			private readonly Stream inputStream;

			public ReadFileToDatabase(BufferPool bufferPool, TransactionalStorage storage, Stream inputStream, string filename)
			{
				this.bufferPool = bufferPool;
				this.inputStream = inputStream;
				this.storage = storage;
				this.filename = filename;
				buffer = bufferPool.TakeBuffer(64 * 1024);
			}

			public Task Execute()
			{
				return inputStream.ReadAsync(buffer)
					.ContinueWith(task =>
					{
						if (task.Result == 0) // nothing left to read
						{
							storage.Batch(accessor => accessor.CompleteFileUpload(filename));
							return task; // task is done
						}

						storage.Batch(accessor =>
						{
							var hashKey = accessor.InsertPage(buffer, task.Result);
							accessor.AssociatePage(filename, hashKey, pos, task.Result);
						});

						pos++;
						return Execute();
					})
					.Unwrap();
			}

			public void Dispose()
			{
				bufferPool.ReturnBuffer(buffer);
			}
		}

		private class FileAccessTool
		{
			private readonly TransactionalStorage storage;
			private readonly BufferPool bufferPool;
			private const int PagesBatchSize = 64;

			public FileAccessTool(TransactionalStorage storage, BufferPool bufferPool)
			{
				this.storage = storage;
				this.bufferPool = bufferPool;
			}

			public Task<object> WriteFile(Stream output, string filename, int fromPage, long? maybeRange)
			{
				FileAndPages fileAndPages = null;
				storage.Batch(accessor => fileAndPages = accessor.GetFile(filename, fromPage, PagesBatchSize));
				if (fileAndPages.Pages.Count == 0)
				{
					return Task.Factory.StartNew(() => new object());
				}

				var offset = 0;
				var pageIndex = 0;
				if (maybeRange != null)
				{
					var range = maybeRange.Value;
					foreach (var page in fileAndPages.Pages)
					{
						if (page.Size > range)
						{
							offset = (int)range;
							break;
						}
						range -= page.Size;
						pageIndex++;
					}

					if (pageIndex >= fileAndPages.Pages.Count)
					{
						return WriteFile(output, filename, fromPage + fileAndPages.Pages.Count, range);
					}
				}

				return WritePages(output, fileAndPages.Pages, pageIndex, offset)
					.ContinueWith(task =>
					{
						if (task.Status != TaskStatus.RanToCompletion)
							task.Wait(); // throw 

						return WriteFile(output, filename, fromPage + fileAndPages.Pages.Count, null);
					}).Unwrap();
			}

			private Task WritePages(Stream output, List<PageInformation> pages, int index, int offset)
			{
				return WritePage(output, pages[index], offset)
					.ContinueWith(task =>
					{
						if (task.Exception != null)
							return task;

						if (index + 1 >= pages.Count)
							return task;

						return WritePages(output, pages, index + 1, 0);
					})
					.Unwrap();
			}

			private Task WritePage(Stream output, PageInformation information, int offset)
			{
				var buffer = bufferPool.TakeBuffer(information.Size);
				try
				{
					storage.Batch(accessor => accessor.ReadPage(information.Key, buffer));
					return output.WriteAsync(buffer, offset, information.Size - offset)
						.ContinueWith(task =>
						{
							bufferPool.ReturnBuffer(buffer);
							return task;
						})
						.Unwrap();
				}
				catch (Exception)
				{
					bufferPool.ReturnBuffer(buffer);
					throw;
				}
			}
		}
	}
}