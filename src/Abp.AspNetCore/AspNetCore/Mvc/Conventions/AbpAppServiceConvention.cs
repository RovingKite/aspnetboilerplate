using System;
using System.Collections.Generic;
using Abp.Application.Services;
using Abp.AspNetCore.Configuration;
using Abp.Extensions;
using Abp.MsDependencyInjection.Extensions;
using Abp.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Abp.Collections.Extensions;
using Abp.Web.Api.ProxyScripting.Generators;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Abp.AspNetCore.Mvc.Conventions
{
    public class AbpAppServiceConvention : IApplicationModelConvention
    {
        private readonly Lazy<AbpAspNetCoreConfiguration> _configuration;

        public AbpAppServiceConvention(IServiceCollection services)
        {
            _configuration = new Lazy<AbpAspNetCoreConfiguration>(() =>
            {
                return services
                    .GetSingletonServiceOrNull<AbpBootstrapper>()
                    .IocManager
                    .Resolve<AbpAspNetCoreConfiguration>();
            }, true);
        }

        public void Apply(ApplicationModel application)
        {
            foreach (var controller in application.Controllers)
            {
                var type = controller.ControllerType.AsType();

                if (typeof(IApplicationService).IsAssignableFrom(type))
                {
                    controller.ControllerName = controller.ControllerName.RemovePostFix("AppService", "ApplicationService", "Service");
                    ConfigureRemoteService(controller);
                }
                else
                {
                    var remoteServiceAtt = ReflectionHelper.GetSingleAttributeOrDefault<RemoteServiceAttribute>(type);
                    if (remoteServiceAtt != null && remoteServiceAtt.IsEnabledFor(type))
                    {
                        ConfigureRemoteService(controller);
                    }
                }
            }
        }

        private void ConfigureRemoteService(ControllerModel controller)
        {
            var configuration = GetControllerSettingOrNull(controller.ControllerType);

            ConfigureApiExplorer(controller);
            ConfigureSelector(controller, configuration);
            ConfigureParameters(controller);
        }

        private void ConfigureParameters(ControllerModel controller)
        {
            foreach (var action in controller.Actions)
            {
                foreach (var prm in action.Parameters)
                {
                    if (prm.BindingInfo != null)
                    {
                        continue;
                    }

                    if (!TypeHelper.IsPrimitiveExtendedIncludingNullable(prm.ParameterInfo.ParameterType))
                    {
                        if (CanUseFormBodyBinding(action))
                        {
                            prm.BindingInfo = BindingInfo.GetBindingInfo(new[] { new FromBodyAttribute() });
                        }
                    }
                }
            }
        }

        private static bool CanUseFormBodyBinding(ActionModel action)
        {
            foreach (var selector in action.Selectors)
            {
                if (selector.ActionConstraints == null)
                {
                    continue;
                }

                foreach (var actionConstraint in selector.ActionConstraints)
                {
                    var httpMethodActionConstraint = actionConstraint as HttpMethodActionConstraint;
                    if (httpMethodActionConstraint == null)
                    {
                        continue;
                    }

                    if (httpMethodActionConstraint.HttpMethods.All(hm => hm.IsIn("GET", "DELETE", "TRACE", "HEAD")))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void ConfigureApiExplorer(ControllerModel controller)
        {
            if (controller.ApiExplorer.GroupName.IsNullOrEmpty())
            {
                controller.ApiExplorer.GroupName = controller.ControllerName;
            }

            if (controller.ApiExplorer.IsVisible == null)
            {
                controller.ApiExplorer.IsVisible = true;
            }

            foreach (var action in controller.Actions)
            {
                ConfigureApiExplorer(action);
            }
        }

        private void ConfigureApiExplorer(ActionModel action)
        {
            if (action.ApiExplorer.IsVisible == null)
            {
                var remoteServiceAtt = ReflectionHelper.GetSingleAttributeOrDefault<RemoteServiceAttribute>(action.ActionMethod);
                action.ApiExplorer.IsVisible = remoteServiceAtt?.IsEnabledFor(action.ActionMethod);
            }
        }

        private void ConfigureSelector(ControllerModel controller, AbpServiceControllerSetting configuration)
        {
            RemoveEmptySelectors(controller.Selectors);

            if (controller.Selectors.Any(selector => selector.AttributeRouteModel != null))
            {
                return;
            }

            var moduleName = GetModuleNameOrDefault(controller.ControllerType.AsType());

            foreach (var action in controller.Actions)
            {
                ConfigureSelector(moduleName, controller.ControllerName, action, configuration);
            }
        }

        private void ConfigureSelector(string moduleName, string controllerName, ActionModel action, AbpServiceControllerSetting configuration)
        {
            RemoveEmptySelectors(action.Selectors);

            if (!action.Selectors.Any())
            {
                AddAbpServiceSelector(moduleName, controllerName, action, configuration);
            }
            else
            {
                NormalizeSelectorRoutes(moduleName, controllerName, action);
            }
        }

        private void AddAbpServiceSelector(string moduleName, string controllerName, ActionModel action, AbpServiceControllerSetting configuration)
        {
            var abpServiceSelectorModel = new SelectorModel
            {
                AttributeRouteModel = CreateAbpServiceAttributeRouteModel(moduleName, controllerName, action)
            };

            var verb = configuration?.UseConventionalHttpVerbs == true
                           ? ProxyScriptingHelper.GetConventionalVerbForMethodName(action.ActionName)
                           : ProxyScriptingHelper.DefaultHttpVerb;

            abpServiceSelectorModel.ActionConstraints.Add(new HttpMethodActionConstraint(new[] { verb }));

            action.Selectors.Add(abpServiceSelectorModel);
        }

        private static void NormalizeSelectorRoutes(string moduleName, string controllerName, ActionModel action)
        {
            foreach (var selector in action.Selectors)
            {
                if (selector.AttributeRouteModel == null)
                {
                    selector.AttributeRouteModel = CreateAbpServiceAttributeRouteModel(
                        moduleName,
                        controllerName,
                        action
                    );
                }
            }
        }

        private string GetModuleNameOrDefault(Type controllerType)
        {
            return GetControllerSettingOrNull(controllerType)?.ModuleName ??
                   AbpServiceControllerSetting.DefaultServiceModuleName;
        }

        private AbpServiceControllerSetting GetControllerSettingOrNull(Type controllerType)
        {
            foreach (var controllerSetting in _configuration.Value.ServiceControllerSettings)
            {
                if (controllerSetting.Assembly == controllerType.Assembly)
                {
                    return controllerSetting;
                }
            }

            return null;
        }

        private static AttributeRouteModel CreateAbpServiceAttributeRouteModel(string moduleName, string controllerName, ActionModel action)
        {
            return new AttributeRouteModel(
                new RouteAttribute(
                    $"api/services/{moduleName}/{controllerName}/{action.ActionName}"
                )
            );
        }

        private static void RemoveEmptySelectors(IList<SelectorModel> selectors)
        {
            selectors
                .Where(IsEmptySelector)
                .ToList()
                .ForEach(s => selectors.Remove(s));
        }

        private static bool IsEmptySelector(SelectorModel selector)
        {
            return selector.AttributeRouteModel == null && selector.ActionConstraints.IsNullOrEmpty();
        }
    }
}