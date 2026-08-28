#if ANDROID
using System.Runtime.CompilerServices;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui.Controls.Handlers.Items;
using Microsoft.Maui.Hosting;
using AView = Android.Views.View;

namespace VardyParty.HomeUi.Views;

/// <summary>
/// CollectionView items on Android default to match_parent height. Each
/// league row then fills the leftover viewport: header + card at the top,
/// empty black cell below — the field "black rectangle clipping the list"
/// on TV. Force wrap_content on every attached item view.
///
/// Do NOT use View.SetTag(int, …) here: Android requires an
/// application-specific R.id key (not GenerateViewId). A generated id
/// threw IllegalArgumentException during handler attach and took the
/// homepage down ("An error occurred. Reloading...").
/// </summary>
public static class HomeUiCollectionView
{
    private static readonly ConditionalWeakTable<RecyclerView, WrapContentItemsListener> Wired = new();

    public static void Register(IMauiHandlersCollection handlers)
    {
        CollectionViewHandler.Mapper.AppendToMapping("VardyPartyWrapItems", (handler, _) =>
        {
            try
            {
                if (handler.PlatformView is not RecyclerView recycler)
                {
                    return;
                }

                recycler.SetClipChildren(false);
                recycler.SetClipToPadding(false);

                if (Wired.TryGetValue(recycler, out WrapContentItemsListener? _))
                {
                    return;
                }

                var listener = new WrapContentItemsListener();
                Wired.Add(recycler, listener);
                recycler.AddOnChildAttachStateChangeListener(listener);
            }
            catch
            {
                // Homepage must still render if wrap wiring fails.
            }
        });
    }

    private sealed class WrapContentItemsListener : Java.Lang.Object, RecyclerView.IOnChildAttachStateChangeListener
    {
        public void OnChildViewAttachedToWindow(AView view)
        {
            ForceWrap(view);
            if (view is ViewGroup group)
            {
                group.SetClipChildren(false);
                group.SetClipToPadding(false);
            }
        }

        public void OnChildViewDetachedFromWindow(AView view)
        {
        }

        private static void ForceWrap(AView view)
        {
            var lp = view.LayoutParameters;
            if (lp is null || lp.Height == ViewGroup.LayoutParams.WrapContent)
            {
                return;
            }

            lp.Height = ViewGroup.LayoutParams.WrapContent;
            view.LayoutParameters = lp;
        }
    }
}
#endif
