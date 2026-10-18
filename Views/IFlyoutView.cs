namespace TrayCer.Views;

/// <summary>
/// A page shown inside the flyout. It hears when it's on screen, so it can refresh then and stop
/// all work when hidden, covered by another page, or closed.
/// </summary>
internal interface IFlyoutView
{
    void OnShown();

    void OnHidden();
}
