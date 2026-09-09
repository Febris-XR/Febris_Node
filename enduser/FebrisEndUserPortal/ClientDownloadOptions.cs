using System;
using Febris.EnumLibrary;

namespace Febris.UserNode.Portal
{
    /// <summary>
    /// Where the portal sends an operator when this node holds no local copy of a client package
    /// (bound from the "<see cref="SectionName"/>" appsettings section).
    ///
    /// The problem this solves. A node's software catalogue starts empty and fills only through a
    /// feed sync, the manual upload path having been removed on 2026-08-31. Nothing forces a
    /// stranger running their own node to run one, so the Software Repository pages were a dead
    /// end on every fresh deployment. Rather than requiring every operator to mirror the
    /// binaries, the portal links out to the project's own download page for anything it does
    /// not hold locally.
    ///
    /// This never overrides a local package. Resolution order is always local first, link second,
    /// so an operator whose feed sync has brought a build in keeps serving it, and an air-gapped
    /// site that
    /// blanks <see cref="BaseUrl"/> behaves exactly as it did before this existed.
    ///
    /// IMPORTANT: rendering a link is NOT a network call. The node makes no outbound request to
    /// resolve or verify these URLs, and nothing is sent anywhere. Only the operator's browser
    /// travels, and only if they click. The node's offline-first posture is unchanged.
    /// </summary>
    /// <remarks>
    /// Lives in the Portal rather than the shared triad on purpose. The published node
    /// export consumes Febris.SharedServices as a NuGet package and ships no shared/ source,
    /// so a class added there could not reach the export without publishing a new package
    /// version. Nothing outside the Portal reads this anyway.
    /// </remarks>
    public class ClientDownloadOptions
    {
        /// <summary>The configuration section these options bind from.</summary>
        public const string SectionName = "ClientDownloads";

        /// <summary>
        /// Root of the public download page. Defaults to the project's own site, so a node that
        /// is never configured still points its operator somewhere useful, which is the entire
        /// point. Blank it to disable link-out entirely: an air-gapped node then shows the plain
        /// empty state and never renders an external URL.
        /// </summary>
        public string BaseUrl { get; set; } = "https://www.febr.is";

        /// <summary>
        /// Anchor fragment for each package kind on the download page. These MUST match the ids
        /// the landing site generator emits per card (tools/generate_site.py, the "slug" field),
        /// or a link lands at the top of the page instead of on the component. Kept as a method
        /// rather than a config map because a mismatch is a build-time bug, not an operator
        /// decision.
        /// </summary>
        public static string AnchorFor(LocalSoftwarePackageType kind)
        {
            switch (kind)
            {
                case LocalSoftwarePackageType.PC: return "pc";
                case LocalSoftwarePackageType.AndroidMobileServer: return "mobile-server";
                case LocalSoftwarePackageType.AndroidMobileCompanion: return "mobile-companion";
                case LocalSoftwarePackageType.CSharp: return "sdk-csharp";
                case LocalSoftwarePackageType.CPP: return "sdk-cpp";
                default: return null;
            }
        }

        /// <summary>
        /// The URL to offer for a kind, or null when link-out is disabled or the kind has no page.
        /// Null is the caller's signal to render the old empty state and nothing else.
        ///
        /// Refuses anything that is not an absolute http(s) URL. A misconfigured value must not
        /// become a relative link that resolves against the node's own host, because that would
        /// silently send an operator to a 404 on their own portal instead of to the download page.
        /// </summary>
        public string DownloadUrlFor(LocalSoftwarePackageType kind)
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                return null;
            }

            string anchor = AnchorFor(kind);
            if (anchor == null)
            {
                return null;
            }

            Uri parsed;
            if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out parsed))
            {
                return null;
            }

            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            // Tolerate a configured value with or without a trailing slash, and one that already
            // carries a path, so an operator can host the page under a subdirectory.
            string root = parsed.GetLeftPart(UriPartial.Path).TrimEnd('/');
            return root + "/#" + anchor;
        }

        /// <summary>
        /// The how-to-use page for a kind. Points at the same public site rather than at each
        /// component's repository directly, so there is ONE place to maintain the mapping: the
        /// site's own documentation section links onward to each repository's README. Routing
        /// straight at a README from here would scatter five URLs across two repositories that
        /// have to be corrected together whenever anything is renamed.
        ///
        /// Null under exactly the same conditions as <see cref="DownloadUrlFor"/>, so an
        /// air-gapped node that blanked the base URL shows local documentation and no link.
        /// </summary>
        public string DocumentationUrlFor(LocalSoftwarePackageType kind)
        {
            string download = DownloadUrlFor(kind);
            if (download == null)
            {
                return null;
            }

            // DownloadUrlFor already validated the root and produced "<root>/#<anchor>".
            int hash = download.LastIndexOf('#');
            return download.Substring(0, hash + 1) + "docs-" + AnchorFor(kind);
        }
    }
}
