using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public enum EffectiveViewportSource { Preset, Settings, FullClient }
public sealed record EffectiveViewport(NormalizedRectangle Region,EffectiveViewportSource Source,
    string Preset,int ClientWidth,int ClientHeight,long Revision);
public sealed record ViewportCalibrationSaveResult(bool SettingsPersisted,bool PresetPersisted,
    bool Reloaded,bool EffectiveVerified,EffectiveViewport? Effective,string? Failure)
{
    public bool Succeeded => SettingsPersisted && PresetPersisted && Reloaded && EffectiveVerified;
}

public static class EffectiveViewportResolver
{
    public static bool IsValid(NormalizedRectangle? r) => r is not null
        && double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height)
        && r.X>=0 && r.Y>=0 && r.Width>=.01 && r.Height>=.01
        && r.X+r.Width<=1.000000001 && r.Y+r.Height<=1.000000001;
    public static EffectiveViewport Resolve(MapRuntimeSettings settings,ViewportCalibrationConfig toml,
        int width,int height,string preset,long revision=0)
    {
        var presetRegion=new NormalizedRectangle {X=toml.MapRegionX,Y=toml.MapRegionY,
            Width=toml.MapRegionWidth,Height=toml.MapRegionHeight};
        if(width>0 && height>0 && toml.ClientWidth==width && toml.ClientHeight==height && IsValid(presetRegion))
            return new(presetRegion,EffectiveViewportSource.Preset,preset,width,height,revision);
        var region=settings.ResolveMapViewportRegion(width,height);
        return IsValid(region) ? new(region!.Clone(),EffectiveViewportSource.Settings,preset,width,height,revision)
            : new(new(){X=0,Y=0,Width=1,Height=1},EffectiveViewportSource.FullClient,preset,width,height,revision);
    }
    public static bool MatchesPixels(NormalizedRectangle expected,NormalizedRectangle actual,int width,int height)
    {
        return IsValid(expected) && IsValid(actual) && width>0 && height>0
            && Math.Abs(expected.X-actual.X)*width<=1 && Math.Abs(expected.Y-actual.Y)*height<=1
            && Math.Abs(expected.X+expected.Width-actual.X-actual.Width)*width<=1
            && Math.Abs(expected.Y+expected.Height-actual.Y-actual.Height)*height<=1;
    }
}
