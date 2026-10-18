using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Planning;

/// <summary>Builds the plan out of the named groups, without any file
/// operation. Only cheap existence checks against the target.</summary>
public static class PlanBuilder
{
    public static OrganizePlan Build(
        IEnumerable<MediaGroup> groups,
        AppSettings settings,
        IEnumerable<RunWarning> warnings)
    {
        var plan = new OrganizePlan
        {
            BaseFolder = settings.TargetFolder,
            Hierarchy = settings.TargetHierarchy,
            Mode = settings.TransferMode,
            Warnings = [.. warnings],
        };

        var defaultStatus = settings.TransferMode == TransferMode.Copy
            ? PlannedStatus.Copy
            : PlannedStatus.Move;

        foreach (var group in groups.OrderBy(g => g.StartLocal))
        {
            if (group.Items.Count == 0)
            {
                continue;
            }

            // A dateless folder carries no prefix in its NAME. It still gets
            // one here when it was split per period — that is what puts it
            // inside the right year or month level. See PlanPaths.
            var prefix = group.IsDateless && !group.IsPeriodScoped
                ? ""
                : PlanPaths.Prefix(group.StartLocal);
            var folder = new PlannedFolder
            {
                Theme = group.Theme,
                DatePrefix = prefix,
                IsCollection = group.IsCollection,
                IsDateless = group.IsDateless,
                InsideFolder = group.InsideFolder,
                TargetPath = PlanPaths.BuildTargetPath(
                    settings.TargetFolder, settings.TargetHierarchy,
                    prefix, group.Theme, group.IsDateless, group.InsideFolder),
            };

            foreach (var item in group.Items.OrderBy(i => i.TakenLocal))
            {
                folder.Items.Add(new PlannedItem { Item = item, Status = defaultStatus });
            }

            plan.Folders.Add(folder);
        }

        PlanConflicts.RefreshFolderState(plan);
        PlanConflicts.MarkNameConflicts(plan);
        return plan;
    }
}
