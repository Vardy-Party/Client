#if ANDROID
using Microsoft.Maui.Handlers;
using Microsoft.Maui;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace VardyParty.Platforms.Android
{
    public class StubBlazorWebViewHandler : ViewHandler<BlazorWebView, global::Android.Views.View>
    {
        public StubBlazorWebViewHandler() : base(ViewHandler.ViewMapper)
        {
        }

        protected override global::Android.Views.View CreatePlatformView()
        {
            var context = MauiContext?.Context
                ?? global::Android.App.Application.Context
                ?? throw new InvalidOperationException("Android context is unavailable.");
            return new global::Android.Views.View(context);
        }
    }
}
#endif