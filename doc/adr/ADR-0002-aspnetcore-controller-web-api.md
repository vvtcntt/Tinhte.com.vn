---
id: ADR-0002
title: Backend theo mô hình ASP.NET Core Web API dạng Controller
status: PROPOSED (Sprint 0) — theo yêu cầu chủ dự án 09/11/2026, chờ Tech Lead duyệt
date: 2026-11-09
related: ADR-0001, WBS-012, WBS-014
---

# ADR-0002 — Backend theo mô hình ASP.NET Core Web API dạng Controller

## Bối cảnh

Khung Sprint 0 ban đầu dùng Minimal API. Chủ dự án yêu cầu backend viết **đúng mô hình ASP.NET Core Web API**, tức kiểu controller như template `dotnet new webapi --use-controllers`, quen thuộc với đội .NET.

## Quyết định

1. **Controller-based:** `builder.Services.AddControllers()` và `app.MapControllers()`. Mọi controller có `[ApiController]` (đặt sẵn trên base `TinhteControllerBase`), kế thừa `TinhteControllerBase`, là `sealed`, tên kết thúc bằng `Controller`, nằm trong namespace `*.Controllers`.
2. **Route tường minh theo hợp đồng v0.3:** `[Route(ApiRoutes.Prefix + "/site")]` + `[HttpGet("config")]` → `/api/v1/site/config`. Không dùng `[controller]` token, vì đường dẫn phải khớp spec.
3. **Vị trí controller:**
   - `Tinhte.Api/Controllers/`: endpoint nền tảng (health, site config).
   - `Tinhte.Modules.<X>/Controllers/`: endpoint nghiệp vụ của module. Assembly module được đăng ký làm **ApplicationPart**.
4. **Controller mỏng:** controller chỉ nhận request, gọi service ở `Application/` của module, rồi trả DTO. Response dùng **DTO có kiểu** (`record`), không trả object vô danh hay entity.
5. **Lỗi theo hợp đồng:**
   - Lỗi nghiệp vụ dùng `Problem(code, title, detail)` của base. Status lấy từ `ErrorCatalog`, content-type `application/problem+json`.
   - Validation của `[ApiController]`: body/tham số sai cú pháp → 400 `BAD_REQUEST`; vi phạm DataAnnotations → 422 `VALIDATION_FAILED` kèm `errors`.
   - Lỗi chưa xử lý → 500 không lộ chi tiết, mã tạm `INTERNAL_ERROR`. **Đề xuất bổ sung mã này vào `x-error-catalog`** vì catalog v0.3 chưa có mã cho 500.
6. **Middleware đa subdomain giữ nguyên (ADR-0001):** phân giải site theo Host và chặn Origin chạy **trước** MVC; controller đọc site qua `Site` của base.
7. **OpenAPI:** `AddOpenApi()`; `/api/v1/openapi.json` chỉ bật ở Development để xem. **Nguồn sự thật vẫn là spec v0.3** (contract-first), không sinh hợp đồng từ code.
8. **Không đặt `[Produces("application/json")]` ở mức base:** nó ghi đè content-type `application/problem+json` của phản hồi lỗi (test đã phát hiện). Nếu cần thì khai báo ở từng action thành công.
9. **Record làm request model:** attribute validation đặt trên **tham số** của positional record (`[Required] string Name`), không đặt `[property: ...]`, vì MVC sẽ ném lỗi.

## Kiểm chứng

`backend/tests/Tinhte.Api.Tests/ControllerConventionTests.cs` kiểm:
- quy ước controller;
- ApplicationPart của module;
- 400/422/409/500 đúng schema Problem;
- tài liệu OpenAPI ở môi trường dev.

Cùng các test định tuyến theo host của ADR-0001: **38/38 PASS**.

## Hệ quả

- **Được:** cấu trúc quen thuộc với đội .NET, dễ gắn filter, model binding, validation; mỗi module tự chứa controller.
- **Mất:** dài dòng hơn Minimal API một chút; phải giữ quy ước route theo hợp đồng thủ công (test quy ước giúp chặn sai sót).
