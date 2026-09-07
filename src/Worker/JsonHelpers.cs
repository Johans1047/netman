using System;
using Newtonsoft.Json;

namespace HotReloadTool.Worker
{
    /// <summary>
    /// JSON serialization helpers using Newtonsoft.Json.
    /// Replaces the minimal SimpleJson from Phase 2 with full JSON serialization.
    /// </summary>
    internal static class JsonHelpers
    {
        public static string Serialize<T>(T obj)
        {
            return JsonConvert.SerializeObject(obj);
        }

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json);
        }
    }
}
