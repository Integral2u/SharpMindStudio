# SharpMind Studio static site

This folder is the public site served by GitHub Pages from `/docs`. There is no build step:
every page is plain HTML and shares `styles.css`. `.nojekyll` keeps GitHub from running
Jekyll and rewriting paths.

## Structure

```text
docs/
  index.html                 hero / product page
  wiki.html                  redirect for old /wiki.html links
  wiki/
    index.html               wiki hub and support matrix
    providers.html           compare Local Docker, Runpod, Vultr, custom
    local-docker.html
    runpod.html
    vultr.html               GPU host setup over SSH (also #custom)
    training.html            pretrain, LoRA, SFT, GPU rules
    troubleshooting.html     exact error messages and fixes
  404.html
  styles.css
  .nojekyll
```

## Editing

- Keep the shared sidebar and footer markup in sync when adding a page. Links from
  `docs/wiki/*` use `../styles.css`, `../index.html`, and sibling page names.
- Use the existing classes: `.guide-card`, `.steps`, `.checklist`, `.callout`,
  `.table-wrap`, `.badge`, `.pill-row`, `.toc`, `.wiki-layout`.
- Do not add external fonts, analytics, or CDN scripts. The site is self-contained.
- Keep the support claims honest: GPU training is LoRA-only; pretraining is CPU;
  unsupported accelerators fall back to the CPU with a log line.

## Preview

Any static file server works. From the repository root:

```powershell
python -m http.server 8090 --directory docs
```

Then open `http://127.0.0.1:8090/`.

## Publish

GitHub Pages should serve the `docs` folder from the public repository. Before publishing,
check that no local paths, real tokens, or IP addresses are in the pages. The wiki uses
placeholders such as `<public-ip>` and `your-name/sharpmind-server:gpu`.
