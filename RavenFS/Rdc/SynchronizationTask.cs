namespace RavenFS.Rdc
{
	using System;
	using System.Collections.Generic;
	using System.Collections.Specialized;
	using System.IO;
	using System.Linq;
	using System.Threading.Tasks;
	using Client;
	using Conflictuality;
	using Extensions;
	using Infrastructure;
	using Multipart;
	using Storage;
	using Util;
	using Wrapper;

	public class SynchronizationTask
	{
		private readonly SynchronizationQueue synchronizationQueue = new SynchronizationQueue();
		private readonly RavenFileSystem localRavenFileSystem;
		private readonly TransactionalStorage storage;
		private readonly ISignatureRepository signatureRepository;
		private readonly SigGenerator sigGenerator;
		private readonly ConflictActifactManager conflictActifactManager;
		private readonly ConflictDetector conflictDetector;
		private readonly ConflictResolver conflictResolver;

		public SynchronizationTask(RavenFileSystem localRavenFileSystem, TransactionalStorage storage, ISignatureRepository signatureRepository, SigGenerator sigGenerator, ConflictActifactManager conflictActifactManager, ConflictDetector conflictDetector, ConflictResolver conflictResolver)
		{
			this.localRavenFileSystem = localRavenFileSystem;
			this.conflictResolver = conflictResolver;
			this.conflictDetector = conflictDetector;
			this.conflictActifactManager = conflictActifactManager;
			this.storage = storage;
			this.sigGenerator = sigGenerator;
			this.signatureRepository = signatureRepository;
		}

		public void SynchronizeDestinations(string fileName)
		{
			foreach (var destination in GetSynchronizationDestinations())
			{
				StartSyncingToAsync(fileName, destination);
			}
		}

		public Task<SynchronizationReport> StartSyncingToAsync(string fileName, string destination)
		{
			if (synchronizationQueue.NumberOfActiveSynchronizationTasksFor(destination) > 1)
			{
				return
					SynchronizationExceptionReport(string.Format("The limit of active synchronizations to {0} server has been achieved.",
					                                             destination));
			}

			var sourceMetadata = GetLocalMetadata(fileName);

			if(sourceMetadata == null)
				return SynchronizationExceptionReport(string.Format("File {0} could not be found", fileName));

			if (sourceMetadata.AllKeys.Contains(SynchronizationConstants.RavenReplicationConflict))
			{
				return SynchronizationExceptionReport(string.Format("File {0} is conflicted", fileName));
			}

			var destinationRavenFileSystemClient = new RavenFileSystemClient(destination);

			return destinationRavenFileSystemClient.GetMetadataForAsync(fileName)
				.ContinueWith(
					getMetadataForAsyncTask =>
						{
							NameValueCollection destinationMetadata = null;

							if (!getMetadataForAsyncTask.IsFaulted)
							{
								destinationMetadata = getMetadataForAsyncTask.Result;
							}

							if (destinationMetadata == null)
							{
								// if file doesn't exist on destination server - upload it there
								return UploadTo(destination, fileName, sourceMetadata);
							}

							var conflict = conflictDetector.Check(destinationMetadata, sourceMetadata);
							var isConflictResolved = conflictResolver.IsResolved(destinationMetadata, conflict);

							// optimization - conflict checking on source side before any changes pushed
							if (conflict != null && !isConflictResolved)
							{
								destinationRavenFileSystemClient.Synchronization.ApplyConflictAsync(fileName, conflict.Current.Version,
																										conflict.Remote.ServerId).Wait();
								return SynchronizationExceptionReport(string.Format("File {0} is conflicted.", fileName));
							}

							var localFileDataInfo = GetLocalFileDataInfo(fileName);

							var remoteSignatureCache = new VolatileSignatureRepository(TempDirectoryTools.Create());
							var localRdcManager = new LocalRdcManager(signatureRepository, storage, sigGenerator);
							var destinationRdcManager = new RemoteRdcManager(destinationRavenFileSystemClient, signatureRepository,
							                                                 remoteSignatureCache);

							var sourceSignatureManifest = localRdcManager.GetSignatureManifest(localFileDataInfo);

							return destinationRdcManager.SynchronizeSignaturesAsync(localFileDataInfo)
								.ContinueWith(
									task =>
										{
											var destinationSignatureManifest = task.Result;

											if (destinationSignatureManifest.Signatures.Count > 0)
											{
												return SynchronizeTo(remoteSignatureCache, destination,
												                     fileName,
												                     sourceSignatureManifest,
												                     sourceMetadata);
											}
											return UploadTo(destination, fileName, sourceMetadata);
										})
								.Unwrap()
								.ContinueWith(
									synchronizationTask =>
										{
											remoteSignatureCache.Dispose();

											return synchronizationTask.Result;
										});
						})
				.Unwrap()
				.ContinueWith(task =>
				              	{
									SynchronizationReport report;
									if (task.Status == TaskStatus.Faulted)
									{
										report =
											new SynchronizationReport
											{
												Exception = task.Exception.ExtractSingleInnerException()
											};
									}
									else
									{
										report = task.Result;
										
										if(task.Result.Exception == null)
										{
											conflictActifactManager.RemoveArtifact(fileName);
										}
									}

				              		return report;
				              	});
		}

		private Task<SynchronizationReport> SynchronizationExceptionReport(string exceptionMessage)
		{
			var tcs = new TaskCompletionSource<SynchronizationReport>();
			tcs.SetResult(new SynchronizationReport()
			              	{
			              		Exception = new SynchronizationException(exceptionMessage)
			              	});
			return tcs.Task;
		}

		private Task<SynchronizationReport> SynchronizeTo(ISignatureRepository remoteSignatureRepository, string destinationServerUrl, string fileName, SignatureManifest sourceSignatureManifest, NameValueCollection sourceMetadata)
		{
			var seedSignatureInfo = SignatureInfo.Parse(sourceSignatureManifest.Signatures.Last().Name);
			var sourceSignatureInfo = SignatureInfo.Parse(sourceSignatureManifest.Signatures.Last().Name);
			var needListGenerator = new NeedListGenerator(remoteSignatureRepository, signatureRepository);

			var localFile = StorageStream.Reading(storage, fileName);

			var needList = needListGenerator.CreateNeedsList(seedSignatureInfo, sourceSignatureInfo);

			return PushByUsingMultipartRequest(destinationServerUrl, fileName, sourceMetadata, localFile, needList, localFile,
			                                   needListGenerator);
		}

		private Task<SynchronizationReport> UploadTo(string destinationServerUrl, string fileName, NameValueCollection localMetadata)
		{
			var sourceFileStream = StorageStream.Reading(storage, fileName);
			var fileSize = sourceFileStream.Length;

			var onlySourceNeed = new List<RdcNeed>
			               	{
			               		new RdcNeed
			               			{
			               				BlockType = RdcNeedType.Source,
			               				BlockLength = (ulong) fileSize,
			               				FileOffset = 0
			               			}
			               	};

			return PushByUsingMultipartRequest(destinationServerUrl, fileName, localMetadata, sourceFileStream, onlySourceNeed,  sourceFileStream);
		}

		private Task<SynchronizationReport> PushByUsingMultipartRequest(string destinationServerUrl, string fileName, NameValueCollection sourceMetadata, Stream sourceFileStream, IList<RdcNeed> needList, params IDisposable[] disposables)
		{
			var multipartRequest = new SynchronizationMultipartRequest(destinationServerUrl, localRavenFileSystem.ServerUrl, fileName, sourceMetadata,
																	   sourceFileStream, needList);

			return multipartRequest.PushChangesAsync()
				.ContinueWith(t =>
				{
					foreach (var disposable in disposables)
					{
						disposable.Dispose();
					}

					t.AssertNotFaulted();

					return t.Result;
				});
		}

		private NameValueCollection GetLocalMetadata(string fileName)
		{
			NameValueCollection result = null;
			try
			{
				storage.Batch(
					accessor =>
					{
						result = accessor.GetFile(fileName, 0, 0).Metadata;
					});
			}
			catch (FileNotFoundException)
			{
				return null;
			}
			return result;
		}

		private DataInfo GetLocalFileDataInfo(string fileName)
		{
			FileAndPages fileAndPages = null;
			
			try
			{
				storage.Batch(accessor => fileAndPages = accessor.GetFile(fileName, 0, 0));
			}
			catch (FileNotFoundException)
			{
				return null;
			}
			return new DataInfo
			{
				CreatedAt = Convert.ToDateTime(fileAndPages.Metadata["Last-Modified"]),
				Length = fileAndPages.TotalSize ?? 0,
				Name = fileAndPages.Name
			};
		}

		private string[] GetSynchronizationDestinations()
		{
			var destionationsConfig = new NameValueCollection();

			storage.Batch(accessor => destionationsConfig = accessor.GetConfig(SynchronizationConstants.RavenReplicationDestinations));

			string[] destinations = destionationsConfig["url"].Split(',');

			return destinations;
		}
	}
}