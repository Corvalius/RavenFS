﻿using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web;
using System.Web.Http;
using RavenFS.Rdc.Wrapper;
using RavenFS.Search;
using RavenFS.Storage;
using RavenFS.Util;
using RavenFS.Web.Infrastructure;

namespace RavenFS.Web.Controllers
{
	public abstract class RavenController : ApiController
	{
		protected class PagingInfo
		{
			public int Start;
			public int PageSize;
		}

		NameValueCollection queryString;
		private PagingInfo paging;


		public BufferPool BufferPool
		{
			get { return RavenFileSystem.Instance.BufferPool; }
		}

		public ISignatureRepository SignatureRepository
		{
			get { return RavenFileSystem.Instance.SignatureRepository; }
		}

		public SigGenerator SigGenerator
		{
			get { return RavenFileSystem.Instance.SigGenerator; }
		}

		private NameValueCollection QueryString
		{
			get { return queryString ?? (queryString = HttpUtility.ParseQueryString(Request.RequestUri.Query)); }
		}

		protected TransactionalStorage Storage
		{
			get { return RavenFileSystem.Instance.Storage; }
		}

		protected IndexStorage Search
		{
			get { return RavenFileSystem.Instance.Search; }
		}

		protected PagingInfo Paging
		{
			get
			{
				if (paging != null)
					return paging;

				int start;
				int.TryParse(QueryString["start"], out start);

				int pageSize;
				int.TryParse(QueryString["pageSize"], out pageSize);

				if (pageSize <= 0 || pageSize >= 256)
					pageSize = 256;

				paging = new PagingInfo
				{
					PageSize = pageSize,
					Start = start
				};
				return paging;
			}
		}

		protected HttpResponseMessage StreamResult(string filename, Stream resultContent)
		{
			var response = new HttpResponseMessage
			{
				Headers =
				{
					TransferEncodingChunked = false
				}
			};
			long length = 0;
			ContentRangeHeaderValue contentRange = null;
			if (Request.Headers.Range != null)
			{
				if (Request.Headers.Range.Ranges.Count != 1)
				{
					throw new InvalidOperationException("Can't handle multiple range values");
				}
				var range = Request.Headers.Range.Ranges.First();
				var from = range.From ?? 0;
				var to = range.To ?? resultContent.Length;

				length = (to - from);

				contentRange = new ContentRangeHeaderValue(from, to, resultContent.Length);
				resultContent = new LimitedStream(resultContent, from, to);
			}
			else
			{
				length = resultContent.Length;
			}

			response.Content = new StreamContent(resultContent)
			{
				Headers =
				{
					ContentDisposition = new ContentDispositionHeaderValue("attachment")
					{
						FileName = filename
					},
					ContentLength = length,
					ContentRange = contentRange,
				}
			};

			return response;
		}

	}
}