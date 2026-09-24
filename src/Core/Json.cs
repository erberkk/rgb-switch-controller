using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace RgbSwitch.Core
{
    public static class Json
    {
        static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        public static string Write(object value) => Serializer().Serialize(value);

        public static object Read(string json) => Serializer().DeserializeObject(json);

        public static Dictionary<string, object> ReadObject(string json) =>
            Read(json) as Dictionary<string, object> ?? new Dictionary<string, object>();

        public static Dictionary<string, object> Obj(object value) => value as Dictionary<string, object>;

        public static IList List(object value) => value as IList;

        public static object Get(this Dictionary<string, object> obj, string key) =>
            obj != null && obj.TryGetValue(key, out var v) ? v : null;

        public static string Str(this Dictionary<string, object> obj, string key) => obj.Get(key) as string;

        public static int? Int(this Dictionary<string, object> obj, string key)
        {
            switch (obj.Get(key))
            {
                case int i: return i;
                case long l: return (int)l;
                case decimal d: return (int)d;
                case double f: return (int)f;
                default: return null;
            }
        }

        public static bool? Bool(this Dictionary<string, object> obj, string key) => obj.Get(key) as bool?;
    }
}
