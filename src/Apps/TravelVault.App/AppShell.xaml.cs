using TravelVault.App.Views;

namespace TravelVault.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("trip", typeof(TripPage));
        Routing.RegisterRoute("document", typeof(DocumentPage));
        Routing.RegisterRoute("edit", typeof(EditDocumentPage));
        Routing.RegisterRoute("review", typeof(ReviewImportPage));
        Routing.RegisterRoute("viewer", typeof(ViewerPage));
        Routing.RegisterRoute("join", typeof(JoinPage));
        Routing.RegisterRoute("travelers", typeof(TravelersPage));
        Routing.RegisterRoute("deleted", typeof(RecentlyDeletedPage));
    }
}
