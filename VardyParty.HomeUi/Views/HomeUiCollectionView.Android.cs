#if ANDROID
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
/// on TV (and the opening view that showed the bottom of Serie A plus the
/// next league, because a window-tall first item plus ScrollTo(Start)
/// shoved the first cards off the top). Force wrap_content on every
/// attached item view.
/// </summary>
public static class HomeUiCollectionView
{
    private static readonly int WrapListenerTag = AView.GenerateViewId();

    public static void Register(IMauiHandlersCollection handlers)
    {
        CollectionViewHandler.Mapper.AppendToMapping("VardyPartyWrapItems", (handler, _) =>
        {
            if (handler.PlatformView is not RecyclerView recycler)
            {
                return;
            }

            recycler.SetClipChildren(false);
            recycler.SetClipToPadding(false);

            if (recycler.GetTag(WrapListenerTag) != null)
            {
                return;
            }

            recycler.SetTag(WrapListenerTag, true);
            recycler.AddOnChildAttachStateChangeListener(new WrapContentItemsListener());
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
