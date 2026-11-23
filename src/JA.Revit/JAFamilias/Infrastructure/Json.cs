using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace JAEsquadrias.Infrastructure
{
    /// <summary>
    /// JSON without external packages (works the same on .NET Framework 4.8 and .NET 8/10):
    /// DataContractJsonSerializer with plain dictionaries ({"a":"b"} instead of key/value lists).
    /// </summary>
    public static class Json
    {
        private static DataContractJsonSerializer Serializer<T>() =>
            new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        public static string Serialize<T>(T value)
        {
            using (var ms = new MemoryStream())
            {
                Serializer<T>().WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default(T);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)Serializer<T>().ReadObject(ms);
        }

        public static T Read<T>(string path) => Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));

        public static void Write<T>(string path, T value)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Serialize(value), new UTF8Encoding(false));
        }
    }
}
