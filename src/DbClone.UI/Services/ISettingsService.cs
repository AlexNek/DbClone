using DbClone.UI.Settings;
using DbClone.UI.ViewModels;

namespace DbClone.UI.Services;

public interface ISettingsService
{
    void ImportLegacy(
        IConnectionStore connectionStore,
        ConnectionViewModel source,
        ConnectionViewModel dest);

    UserSettings Load();

    void Save(UserSettings settings);
}
