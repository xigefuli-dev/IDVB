using IDVBuff.Features.Maps;
using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IDVBuff.Views;

internal sealed record MapListStartupData(
    MapCatalogRevision Revision,
    MapCatalogSnapshot Catalog,
    IReadOnlyList<MapTagGroup> FilterGroups,
    IReadOnlyList<SurveyProjectSummary> SurveyProjects,
    IReadOnlyDictionary<string, BitmapImage> PreviewImages);
