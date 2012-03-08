﻿using System;
using System.Collections.Generic;
using System.Net;
using System.ServiceModel;
using System.Threading.Tasks;
using System.Web.Http.SelfHost;
using RavenFS.Client;
using RavenFS.Extensions;
using RavenFS.Tests.Tools;

namespace RavenFS.Tests
{
	public abstract class MultiHostTestBase : IDisposable
	{
		public static readonly int[] Ports = { 19079, 19081 };

		private readonly IList<IDisposable> disposables = new List<IDisposable>();

		private const string UrlBase = "http://localhost:";

		protected MultiHostTestBase()
		{
			foreach (var port in Ports)
			{
				NonAdminHttp.EnsureCanListenToWhenInNonAdminContext(port);
				HttpSelfHostConfiguration config = null;
				Task.Factory.StartNew(() => // initialize in MTA thread
				{
					config = new HttpSelfHostConfiguration(UrlBase + port + "/")
					{
						MaxReceivedMessageSize = Int64.MaxValue,
						TransferMode = TransferMode.Streamed
					};
					disposables.Add(config);
					var path = "~/" + port;
					IOExtensions.DeleteDirectory(path);
					var ravenFileSystem = new RavenFileSystem(path);
					ravenFileSystem.Start(config);
					disposables.Add(ravenFileSystem);
				})
					.Wait();

				var server = new HttpSelfHostServer(config);
				server.OpenAsync().Wait();

				disposables.Add(server);
			}
		}

		protected RavenFileSystemClient NewClient(int index)
		{
			return new RavenFileSystemClient(UrlBase + Ports[index] + "/");
		}

		#region IDisposable Members

		public void Dispose()
		{
			foreach (var disposable in disposables)
			{
				var httpSelfHostServer = disposable as HttpSelfHostServer;
				if (httpSelfHostServer != null)
					httpSelfHostServer.CloseAsync().Wait();
				disposable.Dispose();
			}
		}

		#endregion
	}
}