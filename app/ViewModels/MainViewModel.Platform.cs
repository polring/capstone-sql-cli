using SqlApp.Services;

namespace SqlApp.ViewModels;

public partial class MainViewModel
{
    public MainViewModel() : this(AssetDiscoveryService.DiscoverBundles(), new SessionStorageService(), new InferenceService()) { }
}
