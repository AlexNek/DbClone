using DbClone.Application.DTOs;
using DbClone.Application.Enums;
using DbClone.UI.ViewModels;

namespace DbClone.UI.Services;

internal static class ConnectionInfoFactory
{
    public static ConnectionInfo FromViewModel(ConnectionViewModel vm)
    {
        var sslMode = vm.SslMode switch
            {
                "Require" => ESslMode.Require,
                "Disable" => ESslMode.Disable,
                _ => ESslMode.Prefer
            };
        return new ConnectionInfo(
            Host: vm.Host,
            Port: vm.PortNumber,
            DatabaseName: vm.DatabaseName,
            Username: vm.Username,
            Password: vm.Password,
            SslMode: sslMode);
    }
}
