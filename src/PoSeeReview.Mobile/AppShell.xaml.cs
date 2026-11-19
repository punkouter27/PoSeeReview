using PoSeeReview.Mobile.Pages;

namespace PoSeeReview.Mobile;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("comic", typeof(ComicPage));
    }
}
