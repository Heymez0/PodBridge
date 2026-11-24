using PodBridge.Protocol;

namespace PodBridge.Ui;

/// <summary>Builds the HomePod list shown in the panel from the HomePods found on the network.</summary>
public static class DeviceGroups
{
    public const string HomePairPrefix = "pair:";
    public const string StereoPrefix = "stereo:";

    /// <summary>
    /// - A stereo pair made in the Home app (two HomePods with the same tight sync ID) becomes one entry. Both get the
    ///   full signal; how the pair splits left and right is up to the HomePods.
    /// - The left/right pair chosen in PodBridge becomes an extra entry that sends the left channel to one HomePod
    ///   and the right channel to the other. Its two HomePods stay in the list on their own as well.
    /// </summary>
    public static List<AirPlayDevice> Build(IEnumerable<AirPlayDevice> devices, string? leftId, string? rightId)
    {
        var singles = devices.Where(d => d.IsHomePod && d.Members == null).ToList();
        var result = new List<AirPlayDevice>();
        var inHomePair = new HashSet<string>();

        foreach (var pair in singles.Where(d => d.PairId != null).GroupBy(d => d.PairId!))
        {
            var both = pair.OrderByDescending(d => d.PairLeader).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToList();
            if (both.Count != 2) continue;
            foreach (var d in both) inHomePair.Add(d.Id);
            string name = both[0].PairName ?? $"{both[0].Name} + {both[1].Name}";
            result.Add(new AirPlayDevice(HomePairPrefix + pair.Key, L.T($"{name} (stereo pair)", $"{name} (stereo çift)"),
                                         both[0].Model, both[0].Address, both[0].Port)
            {
                Members = [new StreamMember(both[0], ChannelMode.Stereo), new StreamMember(both[1], ChannelMode.Stereo)],
            });
        }

        var free = singles.Where(d => !inHomePair.Contains(d.Id)).ToList();
        result.AddRange(free);

        if (leftId != null && rightId != null && leftId != rightId
            && free.FirstOrDefault(d => d.Id == leftId) is { } left
            && free.FirstOrDefault(d => d.Id == rightId) is { } right)
        {
            result.Add(new AirPlayDevice($"{StereoPrefix}{left.Id}+{right.Id}",
                                         L.T($"{left.Name} + {right.Name} (stereo)", $"{left.Name} + {right.Name} (stereo)"),
                                         left.Model, left.Address, left.Port)
            {
                Members = [new StreamMember(left, ChannelMode.Left), new StreamMember(right, ChannelMode.Right)],
            });
        }
        return [.. result.OrderBy(d => d.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>HomePods that can be used for PodBridge's own left/right pair: those not already in a Home-app pair.</summary>
    public static List<AirPlayDevice> PairableSingles(IEnumerable<AirPlayDevice> devices)
    {
        var singles = devices.Where(d => d.IsHomePod && d.Members == null).ToList();
        var paired = singles.Where(d => d.PairId != null).GroupBy(d => d.PairId!).Where(g => g.Count() == 2)
                            .SelectMany(g => g).Select(d => d.Id).ToHashSet();
        return [.. singles.Where(d => !paired.Contains(d.Id)).OrderBy(d => d.Name, StringComparer.CurrentCulture)];
    }
}
