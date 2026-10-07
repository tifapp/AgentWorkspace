using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentOS.App;

// Wrap native controls without fixed cell widths, preserving their keyboard order.
internal sealed class FlowPanel : Panel
{
    private const double Gap = 10;
    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, rowHeight = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width) { y += rowHeight + Gap; x = 0; rowHeight = 0; }
            x += size.Width + Gap; width = Math.Max(width, x - Gap); rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(width, y + rowHeight);
    }
    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > final.Width) { y += rowHeight + Gap; x = 0; rowHeight = 0; }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, final.Width), size.Height));
            x += size.Width + Gap; rowHeight = Math.Max(rowHeight, size.Height);
        }
        return final;
    }
}
