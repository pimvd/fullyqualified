# 5. Marketing site

Status: proposed

- **What:** a static site in `site/`, five pages per language (home, support with FAQ, about, privacy, terms; English at `/`, Dutch under `/nl/`), plus `404.html`, `robots.txt`, `sitemap.xml`, a favicon and share images. It is separate from the app and needs no server code, so any static host in the EU can serve it.
- **No scripts, cookies or third-party requests.** No analytics, fonts or embeds. That keeps the page fast, needs no cookie banner and matches the product's European positioning. Anchors and the FAQ use plain HTML (`details`).
- **Search and sharing:** one canonical URL per page, reciprocal `hreflang` alternates, Open Graph and Twitter tags, a sitemap with alternates, and JSON-LD for `SoftwareApplication` and `FAQPage`. The FAQ in JSON-LD must equal the visible FAQ.
- **Honest claims:** prices are not set, so none are shown; the plans mirror `docs/PLAN.md`; the report is described as evidence, not as NIS2 compliance; the hosted version and cloud connectors are described as planned.
- **Screenshots** are of the real app with fictional example domains.
- **Legal pages are drafts.** Privacy and terms describe what the software does; company details, hosting provider, retention and liability are `[placeholders]`, and a visible notice says a lawyer must review them before the hosted service launches. A test keeps that notice in place until someone removes it on purpose.
- **Pages stay true to the product:** setting names, the recovery command, roles and lifetimes on the pages are checked against the code, the README and `docker-compose.yml`.
- **Tests** (`SiteTests`) serve `site/` over a local HTTP server and check metadata, language pairs, links and assets, structured data against the visible text, sitemap, the absence of third-party loads and of prices, and that tiers and the self-hosting command match `docs/PLAN.md` and the README.
- **Open:** the production domain. `https://fullyqualified.eu` is a placeholder until the name and domain check in the plan is done.
