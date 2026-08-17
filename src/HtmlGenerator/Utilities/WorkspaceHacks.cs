using System;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;

namespace Microsoft.SourceBrowser.HtmlGenerator
{
    public static class WorkspaceHacks
    {
        // Language services (ISemanticFactsService, ISyntaxFactsService, …) are
        // process-wide singletons in Roslyn – all C# projects share one instance,
        // all VB projects share another.  Cache per (language, serviceTypeName) so
        // the expensive Assembly.Load / GetType / MakeGenericMethod / Invoke chain
        // runs at most once per service type rather than once per document.
        private static readonly ConcurrentDictionary<(string language, string serviceType), object>
            s_serviceCache = new();

        public static dynamic GetSemanticFactsService(Document document)
        {
            return GetService(document, "Microsoft.CodeAnalysis.LanguageService.ISemanticFactsService", "Microsoft.CodeAnalysis.Workspaces");
        }

        public static dynamic GetSyntaxFactsService(Document document)
        {
            return GetService(document, "Microsoft.CodeAnalysis.LanguageService.ISyntaxFactsService", "Microsoft.CodeAnalysis.Workspaces");
        }

        public static object GetMetadataAsSourceService(Document document)
        {
            var language = document.Project.Language;
            var workspace = document.Project.Solution.Workspace;
            var serviceAssembly = Assembly.Load("Microsoft.CodeAnalysis.Features");
            var serviceInterfaceType = serviceAssembly.GetType("Microsoft.CodeAnalysis.MetadataAsSource.IMetadataAsSourceService");
            var result = GetService(workspace, language, serviceInterfaceType);
            return result;
        }

        private static object GetService(Workspace workspace, string language, Type serviceType)
        {
            var languageServices = workspace.Services.GetLanguageServices(language);
            var languageServicesType = typeof(HostLanguageServices);
            var genericMethod = languageServicesType.GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance);
            var closedGenericMethod = genericMethod.MakeGenericMethod(serviceType);
            var result = closedGenericMethod.Invoke(languageServices, Array.Empty<object>());
            if (result == null)
            {
                throw new NullReferenceException("Unable to get language service: " + serviceType.FullName + " for " + language);
            }

            return result;
        }

        private static object GetService(Document document, string serviceType, string assemblyName)
        {
            return s_serviceCache.GetOrAdd((document.Project.Language, serviceType), key =>
            {
                var (_, svcType) = key;
                var serviceAssembly = Assembly.Load(assemblyName);
                var serviceInterfaceType = serviceAssembly.GetType(svcType);
                var genericMethod = typeof(LanguageServices).GetMethod(nameof(LanguageServices.GetService), BindingFlags.Public | BindingFlags.Instance);
                var closedGenericMethod = genericMethod.MakeGenericMethod(serviceInterfaceType);
                return closedGenericMethod.Invoke(document.Project.Services, Array.Empty<object>());
            });
        }
    }
}
