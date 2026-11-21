using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyUsb.Playback;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.JellyUsb.Web;

/// <summary>One USB medium of a library, for the "MediaID" view.</summary>
public record LibraryMedia(string MediaId, string Path, bool Present, int MovieCount);

/// <summary>
/// Lists the USB media of a library and their movies. Reads the Jellyfin database only: an absent
/// medium still shows its movies, and the drives are never touched (automount points included).
/// </summary>
public class MediaLibraryService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly MountTable _mountTable;

    public MediaLibraryService(ILibraryManager libraryManager, IDtoService dtoService, MountTable mountTable)
    {
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _mountTable = mountTable;
    }

    /// <summary>The media of the library, sorted by MediaID; empty when the library has none.</summary>
    public IReadOnlyList<LibraryMedia> GetMedia(Guid libraryId, User user)
    {
        return GetMediaFolders(libraryId, user)
            .Select(m => new LibraryMedia(m.MediaId, m.Folder.Path, ActivityService.IsPresent(_mountTable, m.Folder.Path), CountMovies(m.Folder, user)))
            .ToList();
    }

    /// <summary>A page of the movies of one medium, as the regular items API returns them.</summary>
    public QueryResult<BaseItemDto>? GetItems(Guid libraryId, string mediaId, User user, int startIndex, int limit, bool newestFirst)
    {
        var folder = GetMediaFolders(libraryId, user).FirstOrDefault(m => m.MediaId == mediaId).Folder;
        if (folder is null)
        {
            return null;
        }

        var query = new InternalItemsQuery(user)
        {
            AncestorIds = [folder.Id],
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            StartIndex = Math.Max(0, startIndex),
            Limit = Math.Clamp(limit, 1, 200),
            EnableTotalRecordCount = true,
            OrderBy = newestFirst
                ? [(ItemSortBy.DateCreated, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending)]
                : [(ItemSortBy.SortName, SortOrder.Ascending)],
            DtoOptions = new DtoOptions(true)
        };
        var result = _libraryManager.GetItemsResult(query);
        var dtoOptions = new DtoOptions(false)
        {
            Fields = [ItemFields.PrimaryImageAspectRatio],
            EnableImages = true,
            EnableUserData = true,
            ImageTypeLimit = 1,
            ImageTypes = [ImageType.Primary]
        };
        var dtos = _dtoService.GetBaseItemDtos(result.Items, dtoOptions, user, skipVisibilityCheck: true);
        return new QueryResult<BaseItemDto>(query.StartIndex, result.TotalRecordCount, dtos);
    }

    private int CountMovies(Folder folder, User user)
    {
        var result = _libraryManager.GetItemsResult(new InternalItemsQuery(user)
        {
            AncestorIds = [folder.Id],
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            Limit = 0,
            EnableTotalRecordCount = true,
            DtoOptions = new DtoOptions(false)
        });
        return result.TotalRecordCount;
    }

    /// <summary>
    /// The folders of the library that are a medium: the library folders under the base path
    /// (/mnt/usb/MOVIES02), or, for a library folder that is the base path itself, its sub-folders.
    /// </summary>
    private List<(string MediaId, Folder Folder)> GetMediaFolders(Guid libraryId, User user)
    {
        var basePath = Plugin.CurrentConfiguration.BasePath.TrimEnd('/');
        var list = new List<(string MediaId, Folder Folder)>();
        if (_libraryManager.GetItemById(libraryId) is not CollectionFolder library || !library.IsVisible(user))
        {
            return list;
        }

        foreach (var id in library.PhysicalFolderIds)
        {
            if (_libraryManager.GetItemById(id) is not Folder root || string.IsNullOrEmpty(root.Path))
            {
                continue;
            }

            if (root.Path.TrimEnd('/') == basePath)
            {
                foreach (var child in root.Children.OfType<Folder>())
                {
                    if (MediaPresenceChecker.GetMediaId(child.Path, basePath) is { } childId)
                    {
                        list.Add((childId, child));
                    }
                }
            }
            else if (MediaPresenceChecker.GetMediaId(root.Path, basePath) is { } mediaId)
            {
                list.Add((mediaId, root));
            }
        }

        return list
            .GroupBy(m => m.MediaId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(m => m.MediaId, StringComparer.Ordinal)
            .ToList();
    }
}
