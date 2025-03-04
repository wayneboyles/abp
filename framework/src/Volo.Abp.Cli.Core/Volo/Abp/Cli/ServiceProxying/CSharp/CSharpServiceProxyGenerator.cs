﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Volo.Abp.Cli.Commands;
using Volo.Abp.Cli.Http;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Http.Modeling;
using Volo.Abp.Json;

namespace Volo.Abp.Cli.ServiceProxying.CSharp;

public class CSharpServiceProxyGenerator : ServiceProxyGeneratorBase<CSharpServiceProxyGenerator>, ITransientDependency
{
    public const string Name = "CSHARP";

    private const string ProxyDirectory = "ClientProxies";

    private readonly static string[] ServicePostfixes = { "AppService", "ApplicationService", "IntService", "IntegrationService" , "Service"};
    private const string AppServicePrefix = "Volo.Abp.Application.Services";

    private const string NamespacePlaceholder = "<namespace>";
    private const string UsingPlaceholder = "<using>";
    private const string MethodPlaceholder = "<method>";
    private const string PropertyPlaceholder = "<property>";
    private const string ClassNamePlaceholder = "<className>";
    private const string ServiceInterfacePlaceholder = "<serviceInterface>";
    private const string DtoClassNamePlaceholder = "<dtoName>";
    private readonly static string ClassTemplate = "// This file is automatically generated by ABP framework to use MVC Controllers from CSharp" +
                                                   $"{Environment.NewLine}<using>" +
                                                   $"{Environment.NewLine}" +
                                                   $"{Environment.NewLine}// ReSharper disable once CheckNamespace" +
                                                   $"{Environment.NewLine}namespace <namespace>;" +
                                                   $"{Environment.NewLine}" +
                                                   $"{Environment.NewLine}[Dependency(ReplaceServices = true)]" +
                                                   $"{Environment.NewLine}[ExposeServices(typeof(<serviceInterface>), typeof(<className>))]" +
                                                   $"{Environment.NewLine}[IntegrationService]" +
                                                   $"{Environment.NewLine}public partial class <className> : ClientProxyBase<<serviceInterface>>, <serviceInterface>" +
                                                   $"{Environment.NewLine}{{" +
                                                   $"{Environment.NewLine}    <method>" +
                                                   $"{Environment.NewLine}}}" +
                                                   $"{Environment.NewLine}";

    private readonly static string ClassTemplateEmptyPart = "// This file is part of <className>, you can customize it here" +
                                                   $"{Environment.NewLine}// ReSharper disable once CheckNamespace" +
                                                   $"{Environment.NewLine}namespace <namespace>;" +
                                                   $"{Environment.NewLine}" +
                                                   $"{Environment.NewLine}public partial class <className>" +
                                                   $"{Environment.NewLine}{{" +
                                                   $"{Environment.NewLine}}}" +
                                                   $"{Environment.NewLine}";

    private readonly static string InterfaceTemplate = "// This file is automatically generated by ABP framework to use MVC Controllers from CSharp" +
                                                       $"{Environment.NewLine}<using>" +
                                                       $"{Environment.NewLine}" +
                                                       $"{Environment.NewLine}// ReSharper disable once CheckNamespace" +
                                                       $"{Environment.NewLine}namespace <namespace>;" +
                                                       $"{Environment.NewLine}" +
                                                       $"{Environment.NewLine}public interface <serviceInterface> : IApplicationService" +
                                                       $"{Environment.NewLine}{{" +
                                                       $"{Environment.NewLine}    <method>" +
                                                       $"{Environment.NewLine}}}" +
                                                       $"{Environment.NewLine}";

    private readonly static string DtoTemplate = "// This file is automatically generated by ABP framework to use MVC Controllers from CSharp" +
                                                 $"{Environment.NewLine}<using>" +
                                                 $"{Environment.NewLine}" +
                                                 $"{Environment.NewLine}// ReSharper disable once CheckNamespace" +
                                                 $"{Environment.NewLine}namespace <namespace>;" +
                                                 $"{Environment.NewLine}" +
                                                 $"{Environment.NewLine}public <dtoName>" +
                                                 $"{Environment.NewLine}{{" +
                                                 $"{Environment.NewLine}    <property>" +
                                                 $"{Environment.NewLine}}}" +
                                                 $"{Environment.NewLine}";

    private readonly static List<string> ClassUsingNamespaceList = new()
    {
        "using System;",
        "using System.Collections.Generic;",
        "using System.Threading.Tasks;",
        "using Volo.Abp;",
        "using Volo.Abp.Application.Dtos;",
        "using Volo.Abp.Http.Client;",
        "using Volo.Abp.Http.Modeling;",
        "using Volo.Abp.DependencyInjection;",
        "using Volo.Abp.Http.Client.ClientProxying;"
    };

    private readonly static List<string> InterfaceUsingNamespaceList = new()
    {
        "using System;",
        "using System.Collections.Generic;",
        "using System.Threading.Tasks;",
        "using Volo.Abp;",
        "using Volo.Abp.Application.Dtos;",
        "using Volo.Abp.Application.Services;"
    };

    private readonly static List<string> DtoUsingNamespaceList = new()
    {
        "using System;",
        "using System.Collections.Generic;",
        "using Volo.Abp;",
        "using Volo.Abp.Application.Dtos;",
        "using Volo.Abp.ObjectExtending;",
    };

    public CSharpServiceProxyGenerator(
        CliHttpClientFactory cliHttpClientFactory,
        IJsonSerializer jsonSerializer) :
        base(cliHttpClientFactory, jsonSerializer)
    {
    }

    public async override Task GenerateProxyAsync(GenerateProxyArgs args)
    {
        CheckWorkDirectory(args.WorkDirectory);
        CheckFolder(args.Folder);

        if (args.CommandName == RemoveProxyCommand.Name)
        {
            var folder = args.Folder.IsNullOrWhiteSpace() ? ProxyDirectory : args.Folder;
            var folderPath = Path.Combine(args.WorkDirectory, folder);

            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, true);
            }

            Logger.LogInformation($"Delete {GetLoggerOutputPath(folderPath, args.WorkDirectory)}");
            return;
        }

        var applicationApiDescriptionModel = await GetApplicationApiDescriptionModelAsync(args, new ApplicationApiDescriptionModelRequestDto
        {
            IncludeTypes = !args.WithoutContracts
        });

        foreach (var controller in applicationApiDescriptionModel.Modules.Values.SelectMany(x => x.Controllers).
                     Where(x => x.Value.Interfaces.Any() && ServicePostfixes.Any(s => x.Value.Interfaces.Last().Type.EndsWith(s))))
        {
            await GenerateClassFileAsync(args, controller.Value);
        }

        if (!args.WithoutContracts)
        {
            await GenerateDtoFileAsync(args, applicationApiDescriptionModel);
        }

        await CreateJsonFile(args, applicationApiDescriptionModel);
    }

    protected override ServiceType? GetDefaultServiceType(GenerateProxyArgs args)
    {
        return ServiceType.All;
    }

    private async Task CreateJsonFile(GenerateProxyArgs args, ApplicationApiDescriptionModel applicationApiDescriptionModel)
    {
        var folder = args.Folder.IsNullOrWhiteSpace() ? ProxyDirectory : args.Folder;
        var filePath = Path.Combine(args.WorkDirectory, folder, $"{args.Module}-generate-proxy.json");
        using (var writer = new StreamWriter(filePath))
        {
            await writer.WriteAsync(JsonSerializer.Serialize(applicationApiDescriptionModel, indented: true));
        }
    }

    private async Task GenerateClassFileAsync(
        GenerateProxyArgs args,
        ControllerApiDescriptionModel controllerApiDescription)
    {
        var folder = args.Folder.IsNullOrWhiteSpace()
            ? ProxyDirectory + Path.DirectorySeparatorChar + GetTypeNamespace(controllerApiDescription.Type).Replace(".", Path.DirectorySeparatorChar.ToString())
            : args.Folder;

        var appServiceTypeFullName = controllerApiDescription.Interfaces.Last().Type;
        var appServiceTypeName = appServiceTypeFullName.Split('.').Last();
        var clientProxyName = $"{controllerApiDescription.ControllerName}ClientProxy";
        var rootNamespace = $"{GetTypeNamespace(controllerApiDescription.Type)}";

        var classTemplateEmptyPart = new StringBuilder(ClassTemplateEmptyPart);
        classTemplateEmptyPart.Replace(ClassNamePlaceholder, clientProxyName);
        classTemplateEmptyPart.Replace(NamespacePlaceholder, rootNamespace);

        var filePath = Path.Combine(args.WorkDirectory, folder, $"{clientProxyName}.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        if (!File.Exists(filePath))
        {
            using (var writer = new StreamWriter(filePath))
            {
                await writer.WriteAsync(classTemplateEmptyPart.ToString());
            }

            Logger.LogInformation($"Create {GetLoggerOutputPath(filePath, args.WorkDirectory)}");
        }

        var classTemplate = new StringBuilder(ClassTemplate);

        var classUsingNamespaceList = new List<string>(ClassUsingNamespaceList)
        {
            $"using {GetTypeNamespace(appServiceTypeFullName)};"
        };

        if (!controllerApiDescription.IsIntegrationService)
        {
            classTemplate.Replace($"{Environment.NewLine}[IntegrationService]", string.Empty);
        }

        classTemplate.Replace(ClassNamePlaceholder, clientProxyName);
        classTemplate.Replace(NamespacePlaceholder, rootNamespace);
        classTemplate.Replace(ServiceInterfacePlaceholder, appServiceTypeName);

        foreach (var action in controllerApiDescription.Actions.Values.Where(x => ShouldGenerateMethod(appServiceTypeFullName, x)))
        {
            GenerateClassMethod(action, classTemplate, classUsingNamespaceList);
        }

        classTemplate.Replace($"{UsingPlaceholder}", string.Join(Environment.NewLine, classUsingNamespaceList.Distinct().OrderBy(x => x).Select(x => x)));
        classTemplate.Replace($"{Environment.NewLine}{Environment.NewLine}    {MethodPlaceholder}", string.Empty);

        filePath = Path.Combine(args.WorkDirectory, folder, $"{clientProxyName}.Generated.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));
        using (var writer = new StreamWriter(filePath))
        {
            await writer.WriteAsync(classTemplate.ToString());
            Logger.LogInformation($"Create {GetLoggerOutputPath(filePath, args.WorkDirectory)}");
        }

        if (!args.WithoutContracts)
        {
            var interfaceTemplate = new StringBuilder(InterfaceTemplate);

            interfaceTemplate.Replace(ServiceInterfacePlaceholder, appServiceTypeName);

            var @interface = controllerApiDescription.Interfaces.Last();

            var interfaceUsingNamespaceList = new List<string>(InterfaceUsingNamespaceList)
            {
                $"using {GetTypeNamespace(appServiceTypeFullName)};"
            };

            foreach (var method in @interface.Methods)
            {
                GenerateInterfaceMethod(method, interfaceTemplate, interfaceUsingNamespaceList);
            }

            interfaceTemplate.Replace($"{UsingPlaceholder}", string.Join(Environment.NewLine, interfaceUsingNamespaceList.Distinct().OrderBy(x => x).Select(x => x)));
            interfaceTemplate.Replace($"{Environment.NewLine}{Environment.NewLine}    {MethodPlaceholder}", string.Empty);
            interfaceTemplate.Replace(NamespacePlaceholder, rootNamespace);

            filePath = Path.Combine(args.WorkDirectory, folder, $"{appServiceTypeName}.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            using (var writer = new StreamWriter(filePath))
            {
                await writer.WriteAsync(interfaceTemplate.ToString());
                Logger.LogInformation($"Create {GetLoggerOutputPath(filePath, args.WorkDirectory)}");
            }
        }
    }

    private void GenerateClassMethod(
        ActionApiDescriptionModel action,
        StringBuilder clientProxyBuilder,
        List<string> usingNamespaceList)
    {
        var methodBuilder = new StringBuilder();
        var returnTypeName = GetRealTypeName(action.ReturnValue.Type, usingNamespaceList);
        if (action.Name.EndsWith("Async"))
        {
            GenerateAsyncClassMethod(action, returnTypeName, methodBuilder, usingNamespaceList);
        }
        else
        {
            GenerateSyncClassMethod(action, returnTypeName, methodBuilder, usingNamespaceList);
        }

        clientProxyBuilder.Replace(MethodPlaceholder, $"{methodBuilder}{Environment.NewLine}    {MethodPlaceholder}");
    }

    private void GenerateSyncClassMethod(ActionApiDescriptionModel action, string returnTypeName, StringBuilder methodBuilder, List<string> usingNamespaceList)
    {
        methodBuilder.AppendLine($"public virtual {returnTypeName} {action.Name}(<args>)");

        foreach (var parameter in action.Parameters.GroupBy(x => x.Name).Select(x => x.First()))
        {
            methodBuilder.Replace("<args>", $"{GetRealTypeName(parameter.Type, usingNamespaceList)} {parameter.Name}, <args>");
        }

        methodBuilder.Replace("<args>", string.Empty);
        methodBuilder.Replace(", )", ")");

        methodBuilder.AppendLine("    {");
        methodBuilder.AppendLine("        //Client Proxy does not support the synchronization method, you should always use asynchronous methods as a best practice");
        methodBuilder.AppendLine("        throw new System.NotImplementedException(); ");
        methodBuilder.AppendLine("    }");
    }

    private void GenerateAsyncClassMethod(
        ActionApiDescriptionModel action,
        string returnTypeName,
        StringBuilder methodBuilder,
        List<string> usingNamespaceList)
    {
        var returnSign = returnTypeName == "void" ? "Task" : $"Task<{returnTypeName}>";

        methodBuilder.AppendLine($"public virtual async {returnSign} {action.Name}(<args>)");

        foreach (var parameter in action.ParametersOnMethod)
        {
            methodBuilder.Replace("<args>", $"{GetRealTypeName(parameter.Type, usingNamespaceList)} {parameter.Name}, <args>");
        }

        methodBuilder.Replace("<args>", string.Empty);
        methodBuilder.Replace(", )", ")");

        methodBuilder.AppendLine("    {");

        var argsTemplate = "new ClientProxyRequestTypeValue" +
                   $"{Environment.NewLine}        {{<args>" +
                   $"{Environment.NewLine}        }}";

        var args = action.ParametersOnMethod.Any() ? argsTemplate : string.Empty;

        methodBuilder.AppendLine(returnTypeName == "void"
            ? $"        await RequestAsync(nameof({action.Name}), {args});"
            : $"        return await RequestAsync<{returnTypeName}>(nameof({action.Name}), {args});");

        foreach (var parameter in action.ParametersOnMethod)
        {
            methodBuilder.Replace("<args>", $"{Environment.NewLine}            {{ typeof({GetRealTypeName(parameter.Type)}), {parameter.Name} }},<args>");
        }

        methodBuilder.Replace(",<args>", string.Empty);
        methodBuilder.Replace(", )", ")");
        methodBuilder.AppendLine("    }");
    }

    private void GenerateInterfaceMethod(InterfaceMethodApiDescriptionModel action, StringBuilder clientProxyBuilder, List<string> usingNamespaceList)
    {
        var methodBuilder = new StringBuilder();

        var returnTypeName = GetRealTypeName(action.ReturnValue.Type, usingNamespaceList);

        if (action.Name.EndsWith("Async"))
        {
            GenerateAsyncInterfaceMethod(action, returnTypeName, methodBuilder, usingNamespaceList);
        }
        else
        {
            GenerateSyncInterfaceMethod(action, returnTypeName, methodBuilder, usingNamespaceList);
        }

        clientProxyBuilder.Replace(MethodPlaceholder, $"{methodBuilder}{Environment.NewLine}    {MethodPlaceholder}");
    }

    private void GenerateSyncInterfaceMethod(InterfaceMethodApiDescriptionModel action, string returnTypeName, StringBuilder methodBuilder, List<string> usingNamespaceList)
    {
        methodBuilder.AppendLine($"public {returnTypeName} {action.Name}(<args>)");

        foreach (var parameter in action.ParametersOnMethod.GroupBy(x => x.Name).Select(x => x.First()))
        {
            methodBuilder.Replace("<args>", $"{GetRealTypeName(parameter.Type, usingNamespaceList)} {parameter.Name}, <args>");
        }

        if (action.ParametersOnMethod.Count == 0)
        {
            methodBuilder.Replace("(<args>)", "(<args>);");
        }

        methodBuilder.Replace("<args>", string.Empty);
        methodBuilder.Replace(", )", ");");
    }

    private void GenerateAsyncInterfaceMethod(InterfaceMethodApiDescriptionModel action, string returnTypeName, StringBuilder methodBuilder, List<string> usingNamespaceList)
    {
        var returnSign = returnTypeName == "void" ? "Task" : $"Task<{returnTypeName}>";

        methodBuilder.AppendLine($"{returnSign} {action.Name}(<args>)");

        foreach (var parameter in action.ParametersOnMethod)
        {
            methodBuilder.Replace("<args>", $"{GetRealTypeName(parameter.Type, usingNamespaceList)} {parameter.Name}, <args>");
        }

        if (action.ParametersOnMethod.Count == 0)
        {
            methodBuilder.Replace("(<args>)", "(<args>);");
        }

        methodBuilder.Replace("<args>", string.Empty);
        methodBuilder.Replace(", )", ");");
    }

    private bool ShouldGenerateMethod(string appServiceTypeName, ActionApiDescriptionModel action)
    {
        return action.ImplementFrom.StartsWith(AppServicePrefix) ||
               action.ImplementFrom.StartsWith(appServiceTypeName) ||
               IsAppServiceInterface(GetRealTypeName(action.ImplementFrom));
    }

    private async Task GenerateDtoFileAsync(GenerateProxyArgs args, ApplicationApiDescriptionModel applicationApiDescriptionModel)
    {
        var types = new List<string>();

        foreach (var controller in applicationApiDescriptionModel.Modules.Values.First().Controllers)
        {
            types.AddIfNotContains(applicationApiDescriptionModel.Types.Where(x => x.Key.StartsWith($"{GetTypeNamespace(controller.Value.Type)}")).Select(x => x.Key));
        }

        for (var i = 0; i < types.Count; i++)
        {
            var className = types[i];
            if (className.StartsWith("Volo.Abp.Application.Dtos.") && className.Contains("<") && className.Contains(">"))
            {
                types[i] = className.Substring(
                    className.IndexOf("<", StringComparison.Ordinal) + 1,
                    className.IndexOf(">", StringComparison.Ordinal) -
                    className.IndexOf("<", StringComparison.Ordinal) -1);
            }
        }

        types = types.Where(x => !x.StartsWith("System.") && !x.StartsWith("[System.")).Distinct().OrderBy(x => x).ToList();

        foreach (var type in applicationApiDescriptionModel.Types.Where(x => types.Contains(x.Key)))
        {
            var dto = new StringBuilder(DtoTemplate);
            var dtoUsingNamespaceList = new List<string>(DtoUsingNamespaceList)
            {
                $"using {GetTypeNamespace(type.Key)};"
            };

            var genericTypeName = type.Key;
            if (type.Value.GenericArguments != null)
            {
                for (var j = 0; j < type.Value.GenericArguments.Length; j++)
                {
                    genericTypeName = genericTypeName.Replace($"T{j}", type.Value.GenericArguments[j]);
                }
            }

            dto.Replace(NamespacePlaceholder, GetTypeNamespace(genericTypeName));
            dto.Replace(DtoClassNamePlaceholder, type.Value.IsEnum
                ? "enum " + GetRealTypeName(genericTypeName, dtoUsingNamespaceList)
                : "class " + GetRealTypeName(genericTypeName, dtoUsingNamespaceList) +
                  (type.Value.BaseType.IsNullOrEmpty()
                      ? ""
                      : $" : {GetRealTypeName(type.Value.BaseType, dtoUsingNamespaceList)}"));
            var properties = new StringBuilder();
            if (type.Value.IsEnum)
            {
                for (var i = 0; i < type.Value.EnumNames.Length; i++)
                {
                    var enumName = type.Value.EnumNames[i];
                    properties.Append($"{enumName} = {type.Value.EnumValues[i]}");

                    if (i < type.Value.EnumNames.Length - 1)
                    {
                        properties.Append($",");
                        properties.AppendLine();
                        properties.Append("    ");
                    }
                }
            }
            else
            {
                if (!type.Value.Properties.IsNullOrEmpty())
                {
                    for (var i = 0; i < type.Value.Properties.Length; i++)
                    {
                        var property = type.Value.Properties[i];
                        properties.Append("public ");
                        properties.Append(type.Value.GenericArguments.IsNullOrEmpty()
                            ? GetRealTypeName(property.Type, dtoUsingNamespaceList)
                            : GetRealTypeName(genericTypeName, dtoUsingNamespaceList));
                        properties.Append($" {property.Name}");
                        properties.Append(" { get; set; }");
                        if (i < type.Value.Properties.Length - 1)
                        {
                            properties.AppendLine();
                            properties.AppendLine();
                            properties.Append("    ");
                        }
                    }
                }
            }

            dto.Replace($"{UsingPlaceholder}", string.Join(Environment.NewLine, dtoUsingNamespaceList.Distinct().OrderBy(x => x).Select(x => x)));
            dto.Replace(PropertyPlaceholder, properties.ToString());

            var folder = args.Folder.IsNullOrWhiteSpace()
                ? ProxyDirectory + Path.DirectorySeparatorChar + GetTypeNamespace(genericTypeName)
                    .Replace(".", Path.DirectorySeparatorChar.ToString())
                : args.Folder;

            var dtoFileName = GetRealTypeName(genericTypeName).Split("<")[0];
            var filePath = Path.Combine(args.WorkDirectory, folder,
                $"{dtoFileName}.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            using (var writer = new StreamWriter(filePath))
            {
                await writer.WriteAsync(dto.ToString());
                Logger.LogInformation($"Create {GetLoggerOutputPath(filePath, args.WorkDirectory)}");
            }
        }
    }

    private static bool IsAppServiceInterface(string typeName)
    {
        return typeName.StartsWith("I") && ServicePostfixes.Any(typeName.Contains);
    }

    private static string GetTypeNamespace(string typeFullName)
    {
        return typeFullName.Substring(0, typeFullName.LastIndexOf('.'));
    }

    private static string GetRealTypeName(string typeName, List<string> usingNamespaceList = null)
    {
        if (typeName.StartsWith("[") && typeName.EndsWith("]"))
        {
            return GetRealTypeName(typeName.Substring(1, typeName.Length - 2), usingNamespaceList) + "[]";
        }

        if (typeName.StartsWith("{") && typeName.EndsWith("}") && typeName.Contains(":"))
        {
            var dic = typeName.Substring(1, typeName.Length - 2).Split(":");
            var key = GetRealTypeName(dic[0], usingNamespaceList);
            var value = GetRealTypeName(dic[1], usingNamespaceList);
            return $"Dictionary<{key}, {value}>";
        }

        if (!typeName.Contains("<"))
        {
            usingNamespaceList?.AddIfNotContains($"using {GetTypeNamespace(typeName)};");
            return NormalizeTypeName(typeName.Split(".").Last());
        }

        if(usingNamespaceList != null)
        {
            AddGenericTypeUsingNamespace(typeName, usingNamespaceList);
        }
        
        var type = new StringBuilder();
        var s1 = typeName.Split("<");
        for (var i = 0; i < s1.Length; i++)
        {
            if (s1[i].Contains(","))
            {
                var s2 = s1[i].Split(",");
                for (var x = 0; x < s2.Length; x++)
                {
                    type.Append(s2[x].Split(".").Last());
                    if (x < s2.Length - 1)
                    {
                        type.Append(", ");
                    }
                }
            }
            else
            {
                type.Append(s1[i].Split(".").Last());
                if (i < s1.Length - 1)
                {
                    type.Append("<");
                }
            }
        }

        return type.ToString();
    }

    private static void AddGenericTypeUsingNamespace(string typeFullName, List<string> usingNamespaceList)
    {
        if(!typeFullName.Contains("<"))
        {
            usingNamespaceList.AddIfNotContains($"using {GetTypeNamespace(typeFullName)};");
        }

        if (typeFullName.Contains("<") && typeFullName.Contains(">"))
        {
            var left = typeFullName.IndexOf("<", StringComparison.Ordinal);
            var right = typeFullName.LastIndexOf(">", StringComparison.Ordinal);
            var genericTypes = typeFullName.Substring(left + 1, right - left - 1);
            foreach (var genericType in genericTypes.Split(",").Where(x => x.Contains(".")))
            {
                AddGenericTypeUsingNamespace(genericType, usingNamespaceList);
            }
        }
    }

    private static string NormalizeTypeName(string typeName)
    {
        var nullable = string.Empty;
        if (typeName.EndsWith("?"))
        {
            typeName = typeName.TrimEnd('?');
            nullable = "?";
        }

        typeName = typeName switch
        {
            "System.Void" => "void",
            "Void" => "void",
            "System.Boolean" => "bool",
            "Boolean" => "bool",
            "System.String" => "string",
            "String" => "string",
            "System.Int32" => "int",
            "Int32" => "int",
            "System.Int64" => "long",
            "Int64" => "long",
            "System.Double" => "double",
            "Double" => "double",
            "System.Object" => "object",
            "Object" => "object",
            "System.Byte" => "byte",
            "Byte" => "byte",
            "System.Char" => "char",
            "Char" => "char",
            _ => typeName
        };

        return $"{typeName}{nullable}";
    }

    private static void CheckWorkDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new CliUsageException("Specified directory does not exist.");
        }

        var projectFiles = Directory.GetFiles(directory, "*.csproj");
        if (!projectFiles.Any())
        {
            throw new CliUsageException("No project file(csproj) found in the directory.");
        }
    }

    private static void CheckFolder(string folder)
    {
        if (!folder.IsNullOrWhiteSpace() && Path.HasExtension(folder))
        {
            throw new CliUsageException("Option folder should be a directory.");
        }
    }
}
