using Microsoft.Extensions.DependencyInjection;
using Sitecore.DependencyInjection;
using Sitecore.Diagnostics;
using Sitecore.Pipelines.GetChromeData;
using Sitecore.XA.Foundation.Presentation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Sitecore.XA.Foundation.Multisite.Extensions;
using Sitecore.StringExtensions;
using Sitecore.Data.Fields;
using Sitecore.Data.Items;
using Sitecore.Web.UI.PageModes;
using Sitecore.Pipelines.GetPlaceholderRenderings;
using Sitecore.Pipelines;
using Sitecore.Layouts;
using Sitecore.XA.Foundation.Caching;
using Sitecore.XA.Foundation.Presentation.Layout;
using Sitecore.XA.Foundation.SitecoreExtensions.Extensions;
using Sitecore.Web;
using Sitecore.Data;

namespace Sitecore.Support.XA.Foundation.Editing.Pipelines.GetChromeData
{
  public class GetPlaceholderChromeData : Sitecore.XA.Foundation.Editing.Pipelines.GetChromeData.GetPlaceholderChromeData
  {
    public IPresentationContext PresentationContext { get; set; } = ServiceProviderServiceExtensions.GetService<IPresentationContext>(ServiceLocator.ServiceProvider);
    public GetPlaceholderChromeData(Sitecore.XA.Foundation.PlaceholderSettings.Services.ILayoutsPageContext layoutsPageContext) : base(layoutsPageContext)
    {
    }

    public GetPlaceholderChromeData(Sitecore.XA.Foundation.PlaceholderSettings.Services.ILayoutsPageContext layoutsPageContext, Sitecore.XA.Foundation.Abstractions.IContext context) : base(layoutsPageContext, context)
    {
    }

    public override void Process(GetChromeDataArgs args)
    {
      Assert.ArgumentNotNull(args, "args");
      Assert.IsNotNull(args.ChromeData, "Chrome Data");

      if (!"placeholder".Equals(args.ChromeType, StringComparison.OrdinalIgnoreCase))
      {
        return;
      }

      if (Context.Site.IsSxaSite())
      {
        string placeholderKey = args.CustomData["placeHolderKey"] as string;
        Assert.ArgumentNotNull(placeholderKey, "CustomData[\"{0}\"]".FormatWith("placeHolderKey"));

        if (base.CacheEnabled)
        {
          var cacheKey = CacheKey(args);
          var cacheValue = DictionaryCache.Get(cacheKey);

          if (cacheValue != null && cacheValue.Properties.Keys.Count > 0 && ValidateCommands(cacheValue, placeholderKey))
          {
            AssignCachedProperties(args, cacheValue);
            if (!SxaRenderingSourcesKeysExists())
            {
              GetRenderings(args);
            }
            return;
          }
        }

        args.ChromeData.DisplayName = StringUtil.GetLastPart(placeholderKey, '/', placeholderKey);
        var renderings = GetRenderings(args);
        var notPreinjected = !renderings.Any(r => r.Placeholder.Equals(placeholderKey) || r.Placeholder.Equals("/" + placeholderKey));

        if (notPreinjected)
        {
          List<WebEditButton> buttons = GetButtons("/sitecore/content/Applications/WebEdit/Default Placeholder Buttons");
          AddButtonsToChromeData(buttons, args);
        }

        Item placeholderSettingsItem = null;
        bool hasPlaceholderSettings = false;
        if (args.Item != null)
        {
          string layout = ChromeContext.GetLayout(args.Item);
          var getPlaceholderRenderingsArgs = new GetPlaceholderRenderingsArgs(placeholderKey, layout, args.Item.Database)
          {
            OmitNonEditableRenderings = true
          };
          CorePipeline.Run("getPlaceholderRenderings", getPlaceholderRenderingsArgs);
          hasPlaceholderSettings = getPlaceholderRenderingsArgs.HasPlaceholderSettings;
          var allowedRenderings = getPlaceholderRenderingsArgs.PlaceholderRenderings?.Select(i => i.ID.ToShortID().ToString()).ToList() ?? new List<string>();
          if (!args.ChromeData.Custom.ContainsKey(AllowedRenderingsKey))
          {
            args.ChromeData.Custom.Add(AllowedRenderingsKey, allowedRenderings);
          }

          var sxaPlaceholderItems = LayoutsPageContext.GetSxaPlaceholderItems(layout, placeholderKey, args.Item, Context.Device.ID);
          if (sxaPlaceholderItems.Any())
          {
            placeholderSettingsItem = sxaPlaceholderItems.FirstOrDefault();
          }
          else
          {
            var placeholderItem = LayoutsPageContext.GetSxaPlaceholderItem(placeholderKey, args.Item);
            if (placeholderItem != null)
            {
              placeholderSettingsItem = placeholderItem;
            }
            else
            {
              placeholderSettingsItem = LayoutsPageContext.GetPlaceholderItem(getPlaceholderRenderingsArgs.PlaceholderKey, args.Item.Database, layout);
            }
          }
          if (!getPlaceholderRenderingsArgs.PlaceholderKey.EndsWith("*", StringComparison.Ordinal) || sxaPlaceholderItems.Any())
          {
            args.ChromeData.DisplayName = placeholderSettingsItem == null ? StringUtil.GetLastPart(getPlaceholderRenderingsArgs.PlaceholderKey, '/', getPlaceholderRenderingsArgs.PlaceholderKey) : HttpUtility.HtmlEncode(placeholderSettingsItem.DisplayName);
          }
        }
        else
        {
          if (!args.ChromeData.Custom.ContainsKey(AllowedRenderingsKey))
          {
            args.ChromeData.Custom.Add(AllowedRenderingsKey, new List<string>());
          }
        }

        SetEditableChromeDataItem(args, placeholderSettingsItem, hasPlaceholderSettings);
        if (CacheEnabled)
        {
          var cacheKey = CacheKey(args);
          StoreInCache(cacheKey, args);
        }
      }
      else
      {
        base.Process(args);
      }
    }

    protected virtual IEnumerable<RenderingReference> GetRenderings(GetChromeDataArgs args)
    {
      if (args.Item.InheritsFrom(Templates.PartialDesign.ID))
      {
        return GetRenderingsFromSnippet(args.Item, Context.Device, false);
      }
      return GetInjectionsFromItem(PresentationContext.DesignItem, Context.Device);
    }

    protected virtual bool ValidateCommands(DictionaryCacheValue cacheValue, string placeholderKey)
    {
      List<WebEditButton> list;
      if (((list = cacheValue.Properties["Commands"] as List<WebEditButton>) != null) && (list.Count > 0))
      {
        List<WebEditButton> source = (from b in list
                                      where b.Click.Contains("referenceId=")
                                      select b).ToList<WebEditButton>();
        if (source.Any<WebEditButton>() && HttpContext.Current.Request.Form.AllKeys.Contains<string>("layout"))
        {
          LayoutModel layoutModel = this.GetLayoutModel();
          Placeholder placeholder = new Placeholder(placeholderKey);
          RenderingModel model2 = (from r in this.GetCurrentDeviceModel(layoutModel).Renderings.RenderingsCollection
                                   where IsPartOf(new Placeholder(r.Placeholder),placeholder)
                                   orderby r.Placeholder.Length descending
                                   select r).FirstOrDefault<RenderingModel>();
          string referenceId = (model2 != null) ? model2.UniqueId.ToSearchID().ToUpperInvariant() : null;
          return source.Any<WebEditButton>(b => b.Click.Contains("referenceId=" + referenceId));
        }
      }
      return true;
    }

    private bool IsPartOf(Placeholder placeholder1, Placeholder placeholder2)
    {
      char[] trimChars = new char[] { '/' };
      string str = placeholder1.GetPlaceholderPath().TrimStart(trimChars);
      char[] chArray2 = new char[] { '/' };
      return placeholder2.GetPlaceholderPath().TrimStart(chArray2).StartsWith(str);

    }

    protected bool SxaRenderingSourcesKeysExists() =>
    (HttpContext.Current.Items["SXA-RENDERING-SOURCES"] != null);

    protected virtual LayoutModel GetLayoutModel() =>
    new LayoutModel(WebEditUtil.ConvertJSONLayoutToXML(HttpContext.Current.Request.Form["layout"]));

    protected virtual DeviceModel GetCurrentDeviceModel(LayoutModel model)
    {
      ID iD = Context.Device.ID;
      return model.Devices[iD.ToString()];
    }
  }
}