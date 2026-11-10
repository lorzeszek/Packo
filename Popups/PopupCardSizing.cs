namespace Packo.Popups;

internal static class PopupCardSizing
{
    private const double StandardWidth = 320;
    private const double HorizontalMargins = 40; // PackoPopupCardStyle applies 20 on each side.

    public static double GetWidth(Page page) =>
        page.Width > HorizontalMargins ? Math.Min(StandardWidth, page.Width - HorizontalMargins) : StandardWidth;
}
