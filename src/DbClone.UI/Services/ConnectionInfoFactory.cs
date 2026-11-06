using DbClone.Application.DTOs;
using DbClone.UI.ViewModels;

namespace DbClone.UI.Services;

internal static class ConnectionInfoFactory
{
    public static ConnectionInfo FromViewModel(ConnectionViewModel vm) =>
        new(
            Host: vm.Host,
            Port: vm.PortNumber,
            DatabaseName: vm.DatabaseName,
            Username: vm.Username,
            Password: vm.Password,
            SslMode: vm.SslMode);
}
