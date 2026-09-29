using System.Text.Json;

namespace IDVBuff.Features.Maps;

public static partial class MapEntryIdentityResources
{
    private static MapEntryIdentityWall[] ParseNativeWalls(JsonElement rows, int width, int height)
    {
        // Same six columns as the source NPZ: axis, sign, normal, lo, hi, optional edge (-1).
        // These coordinates already belong to the selected author canvas; no grid conversion applies.
        return rows.EnumerateArray().Select(row =>
        {
            if (row.GetArrayLength() != 6)
                throw new InvalidDataException("Native wall segment must contain six values.");
            var axis = Number(row[0]);
            var sign = Number(row[1]);
            var normal = Number(row[2]);
            var lo = Number(row[3]);
            var hi = Number(row[4]);
            var edge = Number(row[5]);
            if (axis is not (0 or 1) || sign is not (-1 or 1)
                || lo < 0 || hi < lo || normal < 0
                || normal >= (axis == 0 ? width : height) || hi >= (axis == 0 ? height : width)
                || edge < -1 || edge > int.MaxValue || edge != Math.Floor(edge))
                throw new InvalidDataException("Native wall segment does not match its author canvas.");
            return new MapEntryIdentityWall((int)axis, (int)sign, normal, lo, hi,
                edge < 0 ? string.Empty : ((int)edge).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }).ToArray();
    }

    private static MapEntryIdentityPassport ParsePassport(JsonElement entry, string mapId, string floorKey)
    {
        var center = Point(entry.GetProperty("center"));
        var unit = Number(entry.GetProperty("unit"));
        if (unit <= 0) throw new InvalidDataException("Entrance grid unit must be positive.");
        var rooms = entry.GetProperty("localInstances").EnumerateArray().Select(room =>
        {
            var ports = room.GetProperty("ports").EnumerateArray().Select(port =>
            {
                var points = port.GetProperty("points").EnumerateArray().Select(Point).ToArray();
                if (points.Length == 0) throw new InvalidDataException("Entrance port has no geometry.");
                return new MapEntryIdentityPort(Text(port, "name"), new MapEntryIdentityPoint(
                    points.Average(p => p.X) * unit + center.X,
                    points.Average(p => p.Y) * unit + center.Y));
            }).ToArray();
            return new MapEntryIdentityRoom(Text(room, "instance"), Text(room, "kind"),
                room.GetProperty("connections").EnumerateArray().Select(e => e.GetRawText()).ToArray(),
                room.GetProperty("connectingPort").GetString() ?? string.Empty,
                Walls(room.GetProperty("knownWallLines")), ports);
        }).ToArray();
        var directions = entry.GetProperty("outwardDirections").EnumerateArray().Select(d => d.GetString() switch
        {
            "N" => MapEntryIdentityDirection.North, "E" => MapEntryIdentityDirection.East,
            "S" => MapEntryIdentityDirection.South, "W" => MapEntryIdentityDirection.West,
            _ => throw new InvalidDataException("Unknown entrance direction.")
        }).ToArray();
        var mainOutline = entry.GetProperty("localInstances").EnumerateArray()
            .Where(room => Text(room, "material").StartsWith("main-entry-", StringComparison.Ordinal))
            .SelectMany(room => room.GetProperty("outline").EnumerateArray().Select(Point))
            .Select(p => new MapEntryIdentityPoint(p.X * unit + center.X, p.Y * unit + center.Y)).ToArray();
        return new MapEntryIdentityPassport(mapId, floorKey, Text(entry, "sourceEntryId"),
            Text(entry, "family"), center, unit, entry.GetProperty("directionKnown").GetBoolean(),
            directions, Walls(entry.GetProperty("knownWallLines")), rooms)
        { MainOutlineSource = mainOutline };
    }

    private static MapEntryCornerRegion ParseMainRegion(JsonElement entry, string mapId, string floor, string profile)
    {
        var unit = PositiveUnit(entry);
        var outline = entry.GetProperty("outline").EnumerateArray().Select(Point).ToArray();
        var nodes = entry.GetProperty("chains").EnumerateArray().Select(chain =>
        {
            var point = Point(chain.GetProperty("points")[1]);
            var rays = chain.GetProperty("rays");
            var isMain = outline.Any(p => Math.Pow(p.X - point.X, 2) + Math.Pow(p.Y - point.Y, 2) <= 9);
            return new MapEntryCornerNode(point, Point(rays[0]), Point(rays[1]), isMain);
        }).ToArray();
        return new MapEntryCornerRegion(mapId, floor, Text(entry, "region"), profile, unit,
            Point(entry.GetProperty("center")), nodes, [], []);
    }

    private static MapEntryCornerRegion ParseLocalRegion(JsonElement entry, string mapId, string floor, string profile)
    {
        var unit = PositiveUnit(entry);
        var turns = entry.GetProperty("turns").EnumerateArray().ToArray();
        var nodes = turns.Select(turn => new MapEntryCornerNode(Point(turn.GetProperty("point")),
            Point(turn.GetProperty("rays")[0]), Point(turn.GetProperty("rays")[1]))).ToArray();
        var pairs = new List<MapEntryCornerPair>();
        for (var i = 0; i < turns.Length; i++)
        {
            var next = turns[i].GetProperty("next").GetInt32();
            if (next >= 0 && next < turns.Length) pairs.Add(new MapEntryCornerPair(i, next));
            else if (next != -1) throw new InvalidDataException("Invalid entrance contour adjacency.");
        }
        var gates = entry.GetProperty("gates").EnumerateArray().Select(g => Point(g.GetProperty("center"))).ToArray();
        return new MapEntryCornerRegion(mapId, floor, Text(entry, "region"), profile, unit,
            new MapEntryIdentityPoint(0, 0), nodes, pairs, gates);
    }

    private static double PositiveUnit(JsonElement entry)
    {
        var unit = Number(entry.GetProperty("unit"));
        return unit > 0 ? unit : throw new InvalidDataException("Entrance grid unit must be positive.");
    }

    private static MapEntryIdentityWall[] Walls(JsonElement rows) => rows.EnumerateArray().Select(row =>
    {
        var axis = row.GetProperty("axis").GetInt32();
        var sign = row.GetProperty("sign").GetInt32();
        var normal = Number(row.GetProperty("normal"));
        var lo = Number(row.GetProperty("lo"));
        var hi = Number(row.GetProperty("hi"));
        var edge = row.GetProperty("edgeId");
        if (axis is not (0 or 1) || sign is not (-1 or 1) || hi <= lo
            || edge.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
            throw new InvalidDataException("Invalid entrance wall plane.");
        return new MapEntryIdentityWall(axis, sign, normal, lo, hi,
            edge.ValueKind == JsonValueKind.String ? edge.GetString()! : edge.GetRawText());
    }).ToArray();

    private static MapEntryIdentityPoint Point(JsonElement value)
    {
        if (value.GetArrayLength() != 2) throw new InvalidDataException("Expected a two dimensional point.");
        return new MapEntryIdentityPoint(Number(value[0]), Number(value[1]));
    }

    private static double Number(JsonElement value)
    {
        var number = value.GetDouble();
        return double.IsFinite(number) ? number : throw new InvalidDataException("Non-finite entrance geometry.");
    }

    private static string Text(JsonElement parent, string key)
    {
        var text = parent.GetProperty(key).GetString();
        return !string.IsNullOrWhiteSpace(text) ? text : throw new InvalidDataException($"Missing entrance field: {key}.");
    }
}
