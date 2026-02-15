#if ANDROID
using Android.Runtime;
using Java.Interop;

namespace AndroidX.Window.Extensions.Core.Util.Function
{
    // Minimal stub to satisfy WebView dependency on androidx.window.extensions.core.util.function.Consumer
    // Some Android TV images ship WebView builds that reference this class but do not include the
    // window-extensions library (or the sidecar JAR). Providing a no-op implementation avoids ClassNotFoundException
    // loops during WebView initialization, which can hang the UI thread on startup.
    // Note: The package name intentionally matches the Java package expected by the WebView.
    [Preserve(AllMembers = true)]
    [JniTypeSignature("androidx/window/extensions/core/util/function/Consumer", GenerateJavaPeer = true)]
    [Register("androidx/window/extensions/core/util/function/Consumer", DoNotGenerateAcw = false)]
    public class Consumer : Java.Lang.Object
    {
        public Consumer()
        {
        }

        protected Consumer(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer)
        {
        }

        [Register("accept", "(Ljava/lang/Object;)V", "GetAccept_Ljava_lang_Object_Handler")]
        public virtual void Accept(Java.Lang.Object? value)
        {
            // No-op stub
        }
    }
}
#endif
