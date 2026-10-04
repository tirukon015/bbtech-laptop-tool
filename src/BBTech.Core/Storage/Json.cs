using System.IO;
using System.Runtime.Serialization.Json;

namespace BBTech.Core.Storage
{
    /// <summary>Framework-only JSON (DataContract) for internal records; no extra DLLs.</summary>
    public static class Json
    {
        public static byte[] ToBytes<T>(T value)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T), Settings()).WriteObject(ms, value);
                return ms.ToArray();
            }
        }

        public static T FromBytes<T>(byte[] data)
        {
            using (var ms = new MemoryStream(data))
                return (T)new DataContractJsonSerializer(typeof(T), Settings()).ReadObject(ms);
        }

        private static DataContractJsonSerializerSettings Settings() => new DataContractJsonSerializerSettings
        {
            DateTimeFormat = new System.Runtime.Serialization.DateTimeFormat("o"),
            UseSimpleDictionaryFormat = true,
        };
    }
}
