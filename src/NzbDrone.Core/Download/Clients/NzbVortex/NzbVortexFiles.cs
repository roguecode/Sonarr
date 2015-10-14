using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Core.Download.Clients.NzbVortex
{
    public class NzbVortexFiles
    {
        [JsonProperty(PropertyName = "files")]
        public List<NzbVortexFile> Files { get; set; }
    }
}
