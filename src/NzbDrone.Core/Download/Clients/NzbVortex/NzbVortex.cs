using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Validation;
using NzbDrone.Core.RemotePathMappings;

namespace NzbDrone.Core.Download.Clients.NzbVortex
{
    public class NzbVortex : UsenetClientBase<NzbVortexSettings>
    {
        private readonly INzbVortexProxy _proxy;

        public NzbVortex(INzbVortexProxy proxy,
                       IHttpClient httpClient,
                       IConfigService configService,
                       IDiskProvider diskProvider,
                       IRemotePathMappingService remotePathMappingService,
                       Logger logger)
            : base(httpClient, configService, diskProvider, remotePathMappingService, logger)
        {
            _proxy = proxy;
        }

        protected override string AddFromNzbFile(RemoteEpisode remoteEpisode, string filename, byte[] fileContent)
        {
            var priority = remoteEpisode.IsRecentEpisode() ? Settings.RecentTvPriority : Settings.OlderTvPriority;

            var response = _proxy.DownloadNzb(fileContent, filename, priority, Settings);

            return response;
        }

        public override string Name
        {
            get
            {
                return "NZBVortex";
            }
        }

        public override IEnumerable<DownloadClientItem> GetItems()
        {
            NzbVortexQueue vortexQueue;

            try
            {
                vortexQueue = _proxy.GetQueue(30, Settings);
            }
            catch (DownloadClientException ex)
            {
                _logger.Warn("Couldn't get download queue. {0}", ex.Message);
                return Enumerable.Empty<DownloadClientItem>();
            }

            var queueItems = new List<DownloadClientItem>();

            foreach (var vortexQueueItem in vortexQueue.Items)
            {
                var queueItem = new DownloadClientItem();

                queueItem.DownloadClient = Definition.Name;
                queueItem.DownloadId = vortexQueueItem.AddUUID ?? vortexQueueItem.Id.ToString();
                queueItem.Category = vortexQueueItem.GroupName;
                queueItem.Title = vortexQueueItem.UiTitle;
                queueItem.TotalSize = vortexQueueItem.TotalDownloadSize;
                queueItem.RemainingSize = vortexQueueItem.TotalDownloadSize - vortexQueueItem.DownloadedSize;
                queueItem.RemainingTime = null;
                queueItem.OutputPath = GetOutputPath(vortexQueueItem);
                
                if (vortexQueueItem.IsPaused)
                {
                    queueItem.Status = DownloadItemStatus.Paused;
                }
                else switch (vortexQueueItem.State)
                {
                    case NzbVortexStateType.Waiting:
                    case NzbVortexStateType.Downloading:
                    case NzbVortexStateType.WaitingForSave:
                        queueItem.Status = DownloadItemStatus.Queued;
                        break;
                    case NzbVortexStateType.Done:
                        queueItem.Status = DownloadItemStatus.Completed;
                        break;
                    case NzbVortexStateType.UncompressFailed:
                    case NzbVortexStateType.CheckFailedDataCorrupt:
                    case NzbVortexStateType.BadlyEncoded:
                        queueItem.Status = DownloadItemStatus.Failed;
                        break;
                    default:
                        queueItem.Status = DownloadItemStatus.Downloading;
                        break;
                }

                if (vortexQueueItem.State == NzbVortexStateType.PasswordRequest)
                {
                    queueItem.IsEncrypted = true;
                }

                queueItems.Add(queueItem);
            }

            return queueItems;
        }

        public override void RemoveItem(string downloadId, bool deleteData)
        {
            // If the NZB wasn't added by Sonarr there won
            int id;

            if (int.TryParse(downloadId, out id))
            {
                _proxy.Remove(id, deleteData, Settings);
            }

            else
            {
                var queue = _proxy.GetQueue(30, Settings);
                var queueItem = queue.Items.FirstOrDefault(c => c.AddUUID == downloadId);

                if (queueItem != null)
                {
                    _proxy.Remove(queueItem.Id, deleteData, Settings);
                }
            }            
        }

        protected List<NzbVortexGroup> GetGroups()
        {
            return _proxy.GetGroups(Settings);
        }

        public override DownloadClientStatus GetStatus()
        {
            // TODO: Get Status for groups
//            var config = _proxy.GetConfig(Settings);
//            var categories = GetCategories(config).ToArray();
//
//            var category = categories.FirstOrDefault(v => v.Name == Settings.TvCategory);
//
//            if (category == null)
//            {
//                category = categories.FirstOrDefault(v => v.Name == "*");
//            }

            var status = new DownloadClientStatus
            {
                IsLocalhost = Settings.Host == "127.0.0.1" || Settings.Host == "localhost"
            };

//            if (category != null)
//            {
//                status.OutputRootFolders = new List<OsPath> { _remotePathMappingService.RemapRemoteToLocal(Settings.Host, category.FullPath) };
//            }

            return status;
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
            failures.AddIfNotNull(TestApiVersion());
            failures.AddIfNotNull(TestAuthentication());
            failures.AddIfNotNull(TestCategory());
        }

        private ValidationFailure TestConnection()
        {
            try
            {
                _proxy.GetVersion(Settings);
            }
            catch (Exception ex)
            {
                _logger.ErrorException(ex.Message, ex);
                return new ValidationFailure("Host", "Unable to connect to NZBVortex");
            }

            return null;
        }

        private ValidationFailure TestApiVersion()
        {
            try
            {
                var response = _proxy.GetApiVersion(Settings);
                var version = new Version(response.ApiLevel);

                if (version.Major < 2 || (version.Major == 2 && version.Minor < 3))
                {
                    return new ValidationFailure("Host", "NZBVortex needs to be updated");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException(ex.Message, ex);
                return new ValidationFailure("Host", "Unable to connect to NZBVortex");
            }

            return null;
        }

        private ValidationFailure TestAuthentication()
        {
            try
            {
                _proxy.GetQueue(1, Settings);
            }
            catch (NzbVortexAuthenticationException ex)
            {
                return new ValidationFailure("ApiKey", "API Key Incorrect");
            }

            return null;
        }

        private ValidationFailure TestCategory()
        {
            var group = GetGroups().FirstOrDefault(c => c.GroupName == Settings.TvCategory);

            if (group == null)
            {
                if (!Settings.TvCategory.IsNullOrWhiteSpace())
                {
                    return new NzbDroneValidationFailure("TvCategory", "Group does not exist")
                    {
                        DetailedDescription = "The Group you entered doesn't exist in NzbVortex. Go to NzbVortex to create it."
                    };
                }
            }

            return null;
        }

        private OsPath GetOutputPath(NzbVortexQueueItem vortexQueueItem)
        {
            var outputPath = _remotePathMappingService.RemapRemoteToLocal(Settings.Host, new OsPath(vortexQueueItem.DestinationPath));

            if (outputPath.FileName == vortexQueueItem.UiTitle)
            {
                return outputPath;
            }

            // If the release isn't done yet, skip the files check and return null
            if (vortexQueueItem.State != NzbVortexStateType.Done)
            {
                return new OsPath(null);
            }

            var filesResponse = _proxy.GetFiles(vortexQueueItem.Id, Settings);

            if (filesResponse.Files.Count > 1)
            {
                _logger.Warn("Download contains multiple files and is not in a job folder: {0}", outputPath);
            }

            return new OsPath(Path.Combine(outputPath.FullPath, filesResponse.Files.First().FileName));
        }
    }
}