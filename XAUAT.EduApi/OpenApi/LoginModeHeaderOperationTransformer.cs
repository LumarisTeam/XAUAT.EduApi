using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using XAUAT.EduApi.Extensions;

namespace XAUAT.EduApi.OpenApi;

/// <summary>
/// 在 OpenAPI 文档里声明 <c>x-loginMode</c> 通用头。
/// <para>
/// 只给真正读取它的控制器加（登录、课表）。校车/电费/饭卡等非教务接口对两个系统通用，
/// 把参数写进它们的文档只会误导客户端。
/// </para>
/// </summary>
public class LoginModeHeaderOperationTransformer : IOpenApiOperationTransformer
{
    public const string Description =
        "Optional academic system selector. 0 or empty = undergraduate (default), 1 = graduate.";

    private static readonly HashSet<string> SupportedControllers =
    [
        "Login",
        "Course"
    ];

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var controllerName = context.Description.ActionDescriptor.RouteValues["controller"];
        if (string.IsNullOrWhiteSpace(controllerName) || !SupportedControllers.Contains(controllerName))
        {
            return Task.CompletedTask;
        }

        // 同属 CourseController，但作息时间表与系统类型无关，不应出现在文档里误导客户端
        if (context.Description.RelativePath?.EndsWith("/ScheduleTime", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];

        if (operation.Parameters.Any(parameter =>
                parameter.In == ParameterLocation.Header &&
                string.Equals(parameter.Name, LoginModeExtensions.HeaderName, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.CompletedTask;
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = LoginModeExtensions.HeaderName,
            In = ParameterLocation.Header,
            Required = false,
            Description = Description,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Default = JsonValue.Create("0"),
                Enum =
                [
                    JsonValue.Create("0")!,
                    JsonValue.Create("1")!
                ]
            }
        });

        return Task.CompletedTask;
    }
}
