namespace AgentCompany.Desktop.Services;

/// <summary>页面进入时回调（由 MainWindow 导航时调用）。</summary>
public interface INavigationAware
{
    void OnNavigatedTo();
}
