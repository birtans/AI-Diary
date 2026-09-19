using AiDiary.Api.Auth;
using AiDiary.Api.Contracts;
using AiDiary.Api.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiDiary.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class SearchController(CurrentUser current, SearchService search) : ControllerBase
{
    /// <summary>Semantic search. The user scope comes from the token, never the request.</summary>
    [HttpPost("search")]
    public async Task<ActionResult<List<SearchResultItem>>> Search(SearchRequest request, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await search.SearchAsync(user.Id, request, ct));
    }
}
