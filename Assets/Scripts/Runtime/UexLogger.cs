using System;
using System.Globalization;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;


namespace UnityEngine
{
    /// <summary>
    /// just copied to delete stacktrace for Debug.Log
    /// https://gist.github.com/yugo-unity/debb0a4b8b48845f2036d4b19b5c57d6
    /// </summary>
    public class UexLogger
    {
        static UexLogger instance = new UexLogger();
        MethodInfo internal_Log = null;

        public UexLogger()
        {
#if DEBUG
            var type = Assembly.Load("UnityEngine").GetType("UnityEngine.DebugLogHandler");
            internal_Log = type.GetMethod("Internal_Log", BindingFlags.Static | BindingFlags.NonPublic, null,
                new System.Type[] { typeof(LogType), typeof(LogOption), typeof(string), typeof(UnityEngine.Object) }, null);
#endif
        }


        [Conditional("DEBUG"), MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Log(object message, UnityEngine.Object context = null)
        {
            instance ??= new UexLogger();
            instance.internal_Log.Invoke(null, new object[] { LogType.Log, LogOption.NoStacktrace, GetString(message), context });
        }

        [Conditional("DEBUG"), MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void LogWarning(object message, UnityEngine.Object context = null)
        {
            instance ??= new UexLogger();
            instance.internal_Log.Invoke(null, new object[] { LogType.Warning, LogOption.NoStacktrace, GetString(message), context });
        }

        [Conditional("DEBUG"), MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void LogError(object message, UnityEngine.Object context = null)
        {
            instance ??= new UexLogger();
            instance.internal_Log.Invoke(null, new object[] { LogType.Error, LogOption.NoStacktrace, GetString(message), context });
        }

        static string GetString(object message)
        {
            if (message == null)
                return "Null";

            if (message is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);

            return message.ToString();
        }
    }
}