#if NET8_0
global using User = Jellyfin.Data.Entities.User;
using Jellyfin.Data.Enums;
#else
global using User = Jellyfin.Database.Implementations.Entities.User;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
#endif
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Frame;

internal static class FrameJellyfinCompatibility
{
    internal static bool HasParentalRestrictions(User user)
    {
#if NET8_0
        var hasRatingLimit = user.MaxParentalAgeRating.HasValue;
#else
        var hasRatingLimit = user.MaxParentalRatingScore.HasValue || user.MaxParentalRatingSubScore.HasValue;
#endif
        return hasRatingLimit ||
            user.GetPreference(PreferenceKind.BlockedTags).Length != 0 ||
            user.GetPreference(PreferenceKind.AllowedTags).Length != 0 ||
            user.GetPreference(PreferenceKind.BlockUnratedItems).Length != 0;
    }

    internal static TaskTriggerInfo IntervalTrigger(TimeSpan interval) => new()
    {
#if NET8_0
        Type = TaskTriggerInfo.TriggerInterval,
#else
        Type = TaskTriggerInfoType.IntervalTrigger,
#endif
        IntervalTicks = interval.Ticks
    };
}
