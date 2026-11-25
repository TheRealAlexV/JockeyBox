using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace JockeyBox.Views.Shell;

/// <summary>
/// Moving a live element between hosts without rebuilding it.
///
/// <para>The one rule that matters: the old parent's content is cleared <b>before</b>
/// the new parent is set. WPF refuses to adopt an element that still has a logical
/// parent ("element already has a logical parent"), and the failure arrives as a
/// throw at the moment of the move, not at the moment of the mistake. Every panel
/// move in the shell goes through here so there is exactly one copy of that order.</para>
/// </summary>
internal static class PanelContent
{
    /// <summary>Put <paramref name="child"/> into <paramref name="host"/>, detaching it first.</summary>
    public static void Reparent(FrameworkElement child, ContentControl host)
    {
        Detach(child);
        host.Content = child;
    }

    /// <summary>Take <paramref name="child"/> out of whatever holds it, leaving it alive.</summary>
    public static void Detach(FrameworkElement child)
    {
        switch (child.Parent)
        {
            case ContentControl owner:
                if (ReferenceEquals(owner.Content, child)) owner.Content = null;
                break;

            case ContentPresenter presenter:
                if (ReferenceEquals(presenter.Content, child)) presenter.Content = null;
                break;

            case Panel panel:
                panel.Children.Remove(child);
                break;

            case Decorator decorator:
                if (ReferenceEquals(decorator.Child, child)) decorator.Child = null;
                break;
        }
    }
}
