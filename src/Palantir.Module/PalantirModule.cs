using Palantir.Domain;
using Palantir.ViewModels;
using Palantir.Views;
using Mithril.Shared.Modules;
using MahApps.Metro.IconPacks;
using Microsoft.Extensions.DependencyInjection;

namespace Palantir;

public sealed class PalantirModule : IMithrilModule
{
    public string Id => "palantir";
    public string DisplayName => "Palantir · Inventory";
    public PackIconLucideKind Icon => PackIconLucideKind.Eye;
    public string? IconUri => "pack://application:,,,/Palantir.Module;component/Resources/palantir.ico";
    public int SortOrder => 900;
    public ActivationMode DefaultActivation => ActivationMode.Lazy;
    public Type ViewType => typeof(PalantirView);
    public Type? SettingsViewType => null;
    public bool IsDeveloperOnly => true;

    public void Register(IServiceCollection services)
    {
        services.AddSingleton<Arda.Wpf.IUiEventSubscriber>(sp => new Arda.Wpf.WpfUiEventSubscriber(
            sp.GetRequiredService<Arda.Contracts.IDomainEventSubscriber>(),
            System.Windows.Application.Current.Dispatcher,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Arda.Wpf.WpfUiEventSubscriber>>()));
        services.AddSingleton<Arda.Wpf.WpfMapPinPresenter>();

        services.AddSingleton<LiveInventoryViewModel>();
        services.AddSingleton<WorldStateViewModel>();
        services.AddSingleton<WorldHealthViewModel>();

        services.AddSingleton<PalantirAttentionSource>();
        services.AddSingleton<IAttentionSource>(sp => sp.GetRequiredService<PalantirAttentionSource>());

        services.AddSingleton<NotificationTesterViewModel>();

        services.AddSingleton<PalantirShellViewModel>();
        services.AddSingleton<PalantirView>(sp => new PalantirView
        {
            DataContext = sp.GetRequiredService<PalantirShellViewModel>(),
        });
    }
}
