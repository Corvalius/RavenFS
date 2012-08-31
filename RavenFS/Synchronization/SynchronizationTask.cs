namespace RavenFS.Synchronization
{
	using System;
	using System.Collections.Generic;
	using System.Collections.Specialized;
	using System.IO;
	using System.Linq;
	using System.Reactive.Linq;
	using System.Threading.Tasks;
	using NLog;
	using Notifications;
	using RavenFS.Client;
	using RavenFS.Extensions;
	using RavenFS.Infrastructure;
	using RavenFS.Storage;
	using RavenFS.Util;
	using Rdc.Wrapper;
	using SynchronizationAction = Notifications.SynchronizationAction;
	using SynchronizationDirection = Notifications.SynchronizationDirection;
	using SynchronizationUpdate = Notifications.SynchronizationUpdate;

	public class SynchronizationTask
	{
		private const int DefaultLimitOfConcurrentSynchronizations = 5;

		private static readonly Logger log = LogManager.GetCurrentClassLogger();

		private readonly SynchronizationQueue synchronizationQueue;
		private readonly TransactionalStorage storage;
		private readonly SigGenerator sigGenerator;
		private readonly NotificationPublisher publisher;

		private readonly IObservable<long> timer = Observable.Interval(TimeSpan.FromMinutes(10));

		public SynchronizationTask(TransactionalStorage storage, SigGenerator sigGenerator, NotificationPublisher publisher)
		{
			this.storage = storage;
			this.sigGenerator = sigGenerator;
			this.publisher = publisher;
			synchronizationQueue = new SynchronizationQueue();

			InitializeTimer();
		}

		public SynchronizationQueue Queue
		{
			get { return synchronizationQueue; }
		}

		private void InitializeTimer()
		{
			timer.Subscribe(tick => SynchronizeDestinationsAsync());
		}

		public Task<Task<DestinationSyncResult>[]> SynchronizeDestinationsAsync(bool forceSyncingContinuation = true)
		{
			log.Debug("Starting to synchronize destinations");

			return Task.Factory.StartNew(() => SynchronizeDestinationsInternal(forceSyncingContinuation).ToArray());
		}

		public Task<SynchronizationReport> SynchronizeFileTo(string fileName, string destinationUrl)
		{
			var destinationClient = new RavenFileSystemClient(destinationUrl);

			return destinationClient.GetMetadataForAsync(fileName)
				.ContinueWith(t =>
				{
				    if (t.Exception != null)
				    {
						log.WarnException("Could not get metadata details for " + fileName +" from " + destinationUrl, t.Exception);
				    	return SynchronizationUtils.SynchronizationExceptionReport(fileName,
				    	                                                           t.Exception.ExtractSingleInnerException().ToString());
				    }

				    var localMetadata = GetLocalMetadata(fileName);

					if(localMetadata == null)
					{
						log.Warn("Could not find local file '{0}' to syncronize");
						return SynchronizationUtils.SynchronizationExceptionReport(fileName,"File does not exists locally");
					}

					var work = DetermineSynchronizationWork(fileName, localMetadata, t.Result);

					if (work == null)
					{
						return SynchronizationUtils.SynchronizationExceptionReport(fileName, "No synchronization work needed");
					}

				    return PerformSynchronization(destinationClient.ServerUrl, work);
				}).Unwrap();
		}

		private IEnumerable<Task<DestinationSyncResult>> SynchronizeDestinationsInternal(bool forceSyncingContinuation)
		{
			foreach (var destination in GetSynchronizationDestinations())
			{
				log.Debug("Starting to synchronize a destination server {0}", destination);

				string destinationUrl = destination;

				if (!CanSynchronizeTo(destinationUrl))
				{
					log.Debug("Could not synchronize to {0} because no synchronization request was available", destination);
					continue;
				}

				var destinationClient = new RavenFileSystemClient(destinationUrl);

				yield return destinationClient.Synchronization.GetLastSynchronizationFromAsync(storage.Id)
					.ContinueWith(etagTask =>
					{
						etagTask.AssertNotFaulted();

						var filesNeedConfirmation = GetSyncingConfigurations(destinationUrl);

						return ConfirmPushedFiles(filesNeedConfirmation, destinationClient)
							.ContinueWith(confirmationTask =>
											{
												confirmationTask.AssertNotFaulted();

												var needSyncingAgain = new List<FileHeader>();

												foreach (var confirmation in confirmationTask.Result)
												{
													if (confirmation.Status == FileStatus.Safe)
													{
														RemoveSyncingConfiguration(confirmation.FileName, destinationUrl);
														log.Debug("Destination server {0} said that file '{1}' is safe", destinationUrl, confirmation.FileName);
													}
													else
													{
														storage.Batch(accessor =>
														{
															var fileHeader = accessor.ReadFile(confirmation.FileName);
															if (FileIsNotBeingUploaded(fileHeader))
															{
																needSyncingAgain.Add(fileHeader);
															}
														});

														log.Debug(
															"Destination server {0} said that file '{1}' is {2}. File will be added to a synchronization queue again.",
															destinationUrl, confirmation.FileName, confirmation.Status);

													}
												}

												return EnqueueMissingUpdates(destinationClient, etagTask.Result, needSyncingAgain)
													.ContinueWith(t =>
													{
														t.AssertNotFaulted();
														return SynchronizePendingFiles(destinationUrl, forceSyncingContinuation);
													});
											}).Unwrap()
							.ContinueWith(
								syncingDestTask =>
									{
										var tasks = syncingDestTask.Result.ToArray();

										if(tasks.Length > 0)
										{
											return Task.Factory.ContinueWhenAll(tasks, t => new DestinationSyncResult
																				{
																					DestinationServer = destinationUrl,
																					Reports = t.Select(syncingTask => syncingTask.Result)
																				});
										}

										return new CompletedTask<DestinationSyncResult>(new DestinationSyncResult
										{
											DestinationServer = destinationUrl
										});
									})
							.Unwrap();
					}).Unwrap()
					.ContinueWith(t =>
					              	{
					              		if (t.Exception != null)
					              		{
					              			var exception = t.Exception.ExtractSingleInnerException();

											log.WarnException(string.Format("Failed to perform a synchronization to a destination {0}", destinationUrl), exception);

											return new DestinationSyncResult
											{
												DestinationServer = destinationUrl,
												Exception = exception
											};
					              		}

					              		var successfullSynchronizationsCount = t.Result.Reports != null
					              		                              	? t.Result.Reports.Where(x => x.Exception == null).Count()
					              		                              	: 0;

										var failedSynchronizationsCount = t.Result.Reports != null
					              		                              	? t.Result.Reports.Where(x => x.Exception != null).Count()
					              		                              	: 0;

					              		if (successfullSynchronizationsCount > 0 || failedSynchronizationsCount > 0)
					              		{
					              			log.Debug(
					              				"Synchronization to a destination {0} has completed. {1} file(s) were synchronized successfully, {2} synchonization(s) were failed",
					              				destinationUrl, successfullSynchronizationsCount, failedSynchronizationsCount);
					              		}

					              		return t.Result;
					              	});
			}
		}

		private Task EnqueueMissingUpdates(RavenFileSystemClient destinationClient, SourceSynchronizationInformation lastEtag, IEnumerable<FileHeader> needSyncingAgain)
		{
			var tcs = new TaskCompletionSource<object>();

			string destinationUrl = destinationClient.ServerUrl;
			List<FileHeader> filesToSynchronization = GetFilesToSynchronization(lastEtag, 100);

			filesToSynchronization.AddRange(needSyncingAgain);

			if (filesToSynchronization.Count == 0)
			{
				tcs.SetResult(null);
				return tcs.Task;
			}

			var determineWorkTasks = new List<Task>();

			for (var i = 0; i < filesToSynchronization.Count; i++)
			{
				var file = filesToSynchronization[i].Name;
				var localMetadata = GetLocalMetadata(file);

				var task = destinationClient.GetMetadataForAsync(file)
					.ContinueWith(t =>
					{
					    if (t.Exception != null)
					    {
					        log.WarnException(
					            string.Format(
					              	"Could not retrieve a metadata of a file '{0}' from {1} in order to determine needed synchronization type",
					              	file,
					              	destinationUrl), t.Exception.ExtractSingleInnerException());
					        return;
					    }

					    var destinationMetadata = t.Result;

					    if (destinationMetadata != null &&
					        destinationMetadata[SynchronizationConstants.RavenSynchronizationConflict] != null
					        && destinationMetadata[SynchronizationConstants.RavenSynchronizationConflictResolution] == null)
					    {
					        log.Debug(
					            "File '{0}' was conflicted on a destination {1} and had no resolution. No need to queue it", file,
					            destinationUrl);
					        return;
					    }

					    if (localMetadata != null &&
					        localMetadata[SynchronizationConstants.RavenSynchronizationConflict] != null)
					    {
					        log.Debug("File '{0}' was conflicted on our side. No need to queue it", file, destinationUrl);
					        return;
					    }

						var work = DetermineSynchronizationWork(file, localMetadata, destinationMetadata);

						if (work == null)
						{
							log.Debug("There was no need to synchronize a file '{0}' to {1}", file, destinationUrl);
							return;
						}

						synchronizationQueue.EnqueueSynchronization(destinationUrl, work);
					});

				determineWorkTasks.Add(task);
			}

			Task.Factory.ContinueWhenAll(determineWorkTasks.ToArray(), x => tcs.SetResult(null));

			return tcs.Task;
		}

		private SynchronizationWorkItem DetermineSynchronizationWork(string file, NameValueCollection localMetadata, NameValueCollection destinationMetadata)
		{
			if (localMetadata[SynchronizationConstants.RavenDeleteMarker] != null)
			{
				var rename = localMetadata[SynchronizationConstants.RavenRenameFile];

				if (rename != null)
				{
					return new RenameWorkItem(file, rename, storage);
				}
				return new DeleteWorkItem(file, storage);
			}
			if (destinationMetadata != null && localMetadata["Content-MD5"] == destinationMetadata["Content-MD5"]) // file exists on dest and has the same content
			{
				// check metadata to detect if any synchronization is needed
				if (localMetadata.AllKeys.Except(new[] { "ETag", "Last-Modified" }).Any(key => !destinationMetadata.AllKeys.Contains(key) || localMetadata[key] != destinationMetadata[key]))
				{
					return new MetadataUpdateWorkItem(file, destinationMetadata, storage);
				}
				return null; // the same content and metadata - no need to synchronize
			}
			return new ContentUpdateWorkItem(file, storage, sigGenerator);
		}

		internal IEnumerable<Task<SynchronizationReport>> SynchronizePendingFiles(string destinationUrl, bool forceSyncingContinuation)
		{
			for (var i = 0; i < AvailableSynchronizationRequestsTo(destinationUrl); i++)
			{
				SynchronizationWorkItem work;
				if (synchronizationQueue.TryDequeuePendingSynchronization(destinationUrl, out work))
				{
					if (synchronizationQueue.IsDifferentWorkForTheSameFileBeingPerformed(work, destinationUrl))
					{
						log.Debug("There was an alredy being performed synchronization of a file '{0}' to {1}", work.FileName,
								  destinationUrl);
						synchronizationQueue.EnqueueSynchronization(destinationUrl, work); // add it again at the end of the queue
					}
					else
					{
						var workTask = PerformSynchronization(destinationUrl, work);

						if (forceSyncingContinuation)
						{
							workTask.ContinueWith(t => SynchronizePendingFiles(destinationUrl, true).ToArray());
						}
						yield return workTask;
					}
				}
				else
				{
					break;
				}
			}
		}

		private Task<SynchronizationReport> PerformSynchronization(string destinationUrl, SynchronizationWorkItem work)
		{
			log.Debug("Starting to perform {0} for a file '{1}' and a destination server {2}", work.GetType().Name, work.FileName,
			          destinationUrl);

			if (!CanSynchronizeTo(destinationUrl))
			{
				log.Debug("The limit of active synchronizations to {0} server has been achieved. Cannot process a file '{1}'.", destinationUrl, work.FileName);

				synchronizationQueue.EnqueueSynchronization(destinationUrl, work);

				return
					SynchronizationUtils.SynchronizationExceptionReport(work.FileName,
																		string.Format(
																			"The limit of active synchronizations to {0} server has been achieved. Cannot process a file '{1}'.",
																			destinationUrl, work.FileName));
			}

			var fileName = work.FileName;
			synchronizationQueue.SynchronizationStarted(work, destinationUrl);
			publisher.Publish(new SynchronizationUpdate
			                  	{
			                  		FileName = work.FileName,
									DestinationServer = destinationUrl,
									SourceServerId = storage.Id,
									Type = work.SynchronizationType,
									Action = SynchronizationAction.Start,
									SynchronizationDirection = SynchronizationDirection.Outgoing
			                  	});

			return work.Perform(destinationUrl)
				.ContinueWith(t =>
				              	{
				              		Queue.SynchronizationFinished(work, destinationUrl);
									CreateSyncingConfiguration(fileName, destinationUrl, work.SynchronizationType);

				              		if (t.Exception != null)
				              		{
				              			log.WarnException(
				              				string.Format(
				              					"An exception was thrown during {0} that was performed for a file '{1}' and a destination {2}",
				              					work.GetType().Name, fileName, destinationUrl), t.Exception.ExtractSingleInnerException());
				              		}

									publisher.Publish(new SynchronizationUpdate
									{
										FileName = work.FileName,
										DestinationServer = destinationUrl,
										SourceServerId = storage.Id,
										Type = work.SynchronizationType,
										Action = SynchronizationAction.Finish,
										SynchronizationDirection = SynchronizationDirection.Outgoing
									});

				              		return t.Result;
				              	});
		}

		private List<FileHeader> GetFilesToSynchronization(SourceSynchronizationInformation destinationsSynchronizationInformationForSource, int take)
		{
			var filesToSynchronization = new List<FileHeader>();

			log.Debug("Getting files to synchronize with ETag greater than {0}",
			          destinationsSynchronizationInformationForSource.LastSourceFileEtag);

			try
			{
				var destinationId = destinationsSynchronizationInformationForSource.DestinationServerInstanceId.ToString();

				var candidatesToSynchronization = Enumerable.Empty<FileHeader>();

				storage.Batch(
					accessor =>
					candidatesToSynchronization =
					accessor.GetFilesAfter(destinationsSynchronizationInformationForSource.LastSourceFileEtag, take)
						.Where(x => x.Metadata[SynchronizationConstants.RavenSynchronizationSource] != destinationId // prevent synchronization back to source
									&& FileIsNotBeingUploaded(x)));
				
				foreach (var file in candidatesToSynchronization)
				{
					var fileName = file.Name;

					if (!candidatesToSynchronization.Any(
						x =>
						x.Metadata[SynchronizationConstants.RavenDeleteMarker] != null &&
						x.Metadata[SynchronizationConstants.RavenRenameFile] == fileName)) // do not synchronize entire file after renaming, process only a tombstone file
					{
						filesToSynchronization.Add(file);
					}
				}
			}
			catch (Exception e)
			{
				log.WarnException(string.Format("Could not get files to synchronize after: " + destinationsSynchronizationInformationForSource.LastSourceFileEtag), e);
			}

			log.Debug("There were {0} file(s) that needed synchronization ({1})", filesToSynchronization.Count,
			          string.Join(",",
			                      filesToSynchronization.Select(
			                      	x => string.Format("{0} [ETag {1}]", x.Name, x.Metadata.Value<Guid>("ETag")))));

			return filesToSynchronization;
		}

		private Task<IEnumerable<SynchronizationConfirmation>> ConfirmPushedFiles(List<string> filesNeedConfirmation, RavenFileSystemClient destinationClient)
		{
			if (filesNeedConfirmation.Count == 0)
			{
				return new CompletedTask<IEnumerable<SynchronizationConfirmation>>(Enumerable.Empty<SynchronizationConfirmation>());
			}
			return destinationClient.Synchronization.ConfirmFilesAsync(filesNeedConfirmation);
		}

		private List<string> GetSyncingConfigurations(string destination)
		{
			IList<SynchronizationDetails> configObjects = new List<SynchronizationDetails>();

			try
			{
				storage.Batch(
					accessor =>
						{
							var configKeys =
								from item in accessor.GetConfigNames()
								where SynchronizationHelper.IsSyncName(item, destination)
								select item;
							configObjects =
								(from item in configKeys
								 select accessor.GetConfigurationValue<SynchronizationDetails>(item)).ToList();
						});
			}
			catch (Exception e)
			{
				log.WarnException(string.Format("Could not get syncing configurations for a destination {0}", destination), e);
			}

			return configObjects.Select(x => x.FileName).ToList();
		}

		private void CreateSyncingConfiguration(string fileName, string destination, SynchronizationType synchronizationType)
		{
			try
			{
				var name = SynchronizationHelper.SyncNameForFile(fileName, destination);
				storage.Batch(accessor => accessor.SetConfigurationValue(name, new SynchronizationDetails
				                                                               	{
				                                                               		DestinationUrl = destination,
				                                                               		FileName = fileName,
																					Type = synchronizationType
				                                                               	}));
			}
			catch (Exception e)
			{
				log.WarnException(
					string.Format("Could not create syncing configurations for a file {0} and destination {1}", fileName, destination), e);
			}
		}

		private void RemoveSyncingConfiguration(string fileName, string destination)
		{
			try
			{
				var name = SynchronizationHelper.SyncNameForFile(fileName, destination);
				storage.Batch(accessor => accessor.DeleteConfig(name));
			}
			catch (Exception e)
			{
				log.WarnException(
					string.Format("Could not remove syncing configurations for a file {0} and a destination {1}", fileName, destination), e);
			}
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

		private IEnumerable<string> GetSynchronizationDestinations()
		{
			var destinationsConfigExists = false;
			storage.Batch(accessor => destinationsConfigExists = accessor.ConfigExists(SynchronizationConstants.RavenSynchronizationDestinations));

			if (!destinationsConfigExists)
			{
				log.Debug("Configuration Raven/Synchronization/Destinations does not exist");
				return Enumerable.Empty<string>();
			}

			var destionationsConfig = new NameValueCollection();

			storage.Batch(accessor => destionationsConfig = accessor.GetConfig(SynchronizationConstants.RavenSynchronizationDestinations));

			var destinations = destionationsConfig.GetValues("url");

			if (destinations == null)
			{
				log.Warn("Invalid Raven/Synchronization/Destinations configuration");
				return Enumerable.Empty<string>();
			}

			for (int i = 0; i < destinations.Length; i++)
			{
				if (destinations[i].EndsWith("/"))
				{
					destinations[i] = destinations[i].Substring(0, destinations[i].Length - 1);
				}
			}

			if (destinations.Length == 0)
			{
				log.Warn("Configuration Raven/Synchronization/Destinations does not contain any destination");
			}

			return destinations;
		}

		private bool CanSynchronizeTo(string destination)
		{
			return LimitOfConcurrentSynchronizations() > synchronizationQueue.NumberOfActiveSynchronizationTasksFor(destination);
		}

		private int AvailableSynchronizationRequestsTo(string destination)
		{
			return LimitOfConcurrentSynchronizations() - synchronizationQueue.NumberOfActiveSynchronizationTasksFor(destination);
		}

		private int LimitOfConcurrentSynchronizations()
		{
			bool limit = false;
			int configuredLimit = 0;

			storage.Batch(
				accessor =>
				limit = accessor.TryGetConfigurationValue(SynchronizationConstants.RavenSynchronizationLimit, out configuredLimit));

			return limit ? configuredLimit : DefaultLimitOfConcurrentSynchronizations;
		}

		private static bool FileIsNotBeingUploaded(FileHeader header)
		{			// do not synchronize files that are being uploaded
			return header.TotalSize != null && header.TotalSize == header.UploadedSize
				// even if the file is uploaded make sure file has Content-MD5 (which calculation that might take some time)
				// it's necessary to determine synchronization type and ensures right ETag
				   && (header.Metadata[SynchronizationConstants.RavenDeleteMarker] == null ? header.Metadata["Content-MD5"] != null : true);
		}
	}
}