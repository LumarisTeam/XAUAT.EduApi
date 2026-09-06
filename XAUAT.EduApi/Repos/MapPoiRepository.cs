using EduApi.Data;
using EduApi.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace XAUAT.EduApi.Repos;

public class MapPoiRepository(IDbContextFactory<EduContext> contextFactory) : IMapPoiRepository
{
    public async Task<List<MapPoiModel>> GetAllActiveAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.MapPois
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<List<MapPoiModel>> GetByCategoryAsync(string category)
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.MapPois
            .AsNoTracking()
            .Where(p => p.IsActive && p.Category == category)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<List<MapPoiModel>> GetByCampusAsync(string campus)
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.MapPois
            .AsNoTracking()
            .Where(p => p.IsActive && p.Campus == campus)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<MapPoiModel?> GetByIdAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.MapPois
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id)
            .ConfigureAwait(false);
    }

    public async Task<List<MapPoiModel>> SearchAsync(string keyword)
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.MapPois
            .AsNoTracking()
            .Where(p => p.IsActive &&
                        (p.Name.ToLower().Contains(keyword) ||
                         (p.Description != null && p.Description.ToLower().Contains(keyword)) ||
                         (p.Address != null && p.Address.ToLower().Contains(keyword)) ||
                         p.Category.ToLower().Contains(keyword)))
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .Take(50)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<List<string>> GetCategoriesAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.MapPois
            .AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => p.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<List<string>> GetCampusesAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        return await context.MapPois
            .AsNoTracking()
            .Where(p => p.IsActive && p.Campus != null)
            .Select(p => p.Campus!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task UpsertAsync(MapPoiModel poi)
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var existing = await context.MapPois
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(p => p.Name == poi.Name)
            .ConfigureAwait(false);

        if (existing is null)
        {
            await context.MapPois.AddAsync(poi).ConfigureAwait(false);
        }
        else
        {
            ApplyImport(existing, poi);
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task UpsertRangeAsync(IEnumerable<MapPoiModel> pois)
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var distinctPois = pois
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();

        var names = distinctPois.Select(p => p.Name).ToList();
        var existingByName = (await context.MapPois
                .Where(p => names.Contains(p.Name))
                .OrderBy(p => p.Id)
                .ToListAsync()
                .ConfigureAwait(false))
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var poi in distinctPois)
        {
            if (existingByName.TryGetValue(poi.Name, out var existing))
            {
                ApplyImport(existing, poi);
            }
            else
            {
                await context.MapPois.AddAsync(poi).ConfigureAwait(false);
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task UpdateAsync(MapPoiModel poi)
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        context.MapPois.Update(poi);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task<int> RemoveAllAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var all = await context.MapPois.ToListAsync().ConfigureAwait(false);
        context.MapPois.RemoveRange(all);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return all.Count;
    }

    private static void ApplyImport(MapPoiModel target, MapPoiModel source)
    {
        source.Id = target.Id;
        source.CreatedAt = target.CreatedAt;

        target.Category = source.Category;
        target.Latitude = source.Latitude;
        target.Longitude = source.Longitude;
        target.Description = source.Description;
        target.Address = source.Address;
        target.Campus = source.Campus;
        target.Icon = source.Icon;
        target.IsActive = source.IsActive;
        target.SortOrder = source.SortOrder;
        target.UpdatedAt = source.UpdatedAt;
    }
}
