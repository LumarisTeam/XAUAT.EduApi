using EduApi.Data.Models;

namespace XAUAT.EduApi.Interfaces;

public interface ILoginService
{
    Task<LoginResponse> LoginAsync(string username, string password, string language = "zh");

    Task<LoginResponse> LoginAsync(
        string username, string password, MfaRequestOptions options, string language = "zh");
}
