using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tinhte.Platform.Modules;

/// <summary>
/// Ranh giới module (WBS-012, ADR-0002). Mỗi module là một class library gồm:
///   Controllers/   controller kế thừa TinhteControllerBase, route theo đường dẫn hợp đồng v0.3
///   Application/   service xử lý nghiệp vụ (controller mỏng, chỉ gọi service)
///   Domain/, Infrastructure/  entity, repository, EF Core... (thêm khi có nghiệp vụ)
/// Assembly của module được đăng ký làm ApplicationPart nên controller tự được MVC phát hiện.
/// Module chỉ tham chiếu Tinhte.Platform, không tham chiếu module khác (test kiến trúc kiểm);
/// giao tiếp giữa module qua sự kiện outbox (WBS-020).
/// </summary>
public interface IModule
{
    string Name { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);
}
