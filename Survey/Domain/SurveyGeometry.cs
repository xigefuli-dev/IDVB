namespace IDVBuff.Survey.Domain;

public readonly record struct SurveyWorldPoint(double X, double Y);

public readonly record struct SurveyWorldRect(
    double X,
    double Y,
    double Width,
    double Height)
{
    public bool IsValid =>
        double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width >= 0d
        && Height >= 0d;

    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public readonly record struct SurveyPixelRect(
    double X,
    double Y,
    double Width,
    double Height)
{
    public bool IsValid =>
        double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width > 0d
        && Height > 0d;
}

public readonly record struct SurveyLayerTransform(
    double TranslationX,
    double TranslationY,
    double RotationDegrees,
    double ScaleX,
    double ScaleY)
{
    public static SurveyLayerTransform Identity { get; } = new(0d, 0d, 0d, 1d, 1d);

    public bool IsValid =>
        double.IsFinite(TranslationX)
        && double.IsFinite(TranslationY)
        && double.IsFinite(RotationDegrees)
        && double.IsFinite(ScaleX)
        && double.IsFinite(ScaleY)
        && ScaleX > 0d
        && ScaleY > 0d;

    public SurveyWorldPoint Transform(SurveyWorldPoint point)
    {
        var radians = RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var scaledX = point.X * ScaleX;
        var scaledY = point.Y * ScaleY;
        return new SurveyWorldPoint(
            scaledX * cosine - scaledY * sine + TranslationX,
            scaledX * sine + scaledY * cosine + TranslationY);
    }

    public SurveyWorldPoint InverseTransform(SurveyWorldPoint point)
    {
        if (!IsValid)
            throw new InvalidOperationException("Cannot invert an invalid survey layer transform.");
        var radians = -RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var translatedX = point.X - TranslationX;
        var translatedY = point.Y - TranslationY;
        return new SurveyWorldPoint(
            (translatedX * cosine - translatedY * sine) / ScaleX,
            (translatedX * sine + translatedY * cosine) / ScaleY);
    }
}
