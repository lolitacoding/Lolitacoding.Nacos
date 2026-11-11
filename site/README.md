# 官网

Lolitacoding 的官网，源码就在本仓库的 `site/` 下，由 GitHub Pages 从 `main` 分支发布：

<https://lolitacoding.github.io/Lolitacoding.Nacos/>

单页、手写 HTML/CSS，没有构建步骤、没有框架、没有外部字体或 CDN。改完直接推送，
`.github/workflows/pages.yml` 会把 `site/` 作为静态站点上传，一两分钟后线上更新。

## 结构

| 文件 | 作用 |
| --- | --- |
| `index.html` | 整站内容：定位、特性、版本对照、代码示例、包列表、快速开始、文档入口。 |
| `assets/styles.css` | 全部样式，含响应式断点、`prefers-reduced-motion` 和打印样式。 |
| `assets/main.js` | 代码标签页（含方向键切换）与复制按钮；禁用 JS 时所有面板仍然可见。 |
| `assets/favicon.svg` | 站点图标，也是页头的品牌标记。 |
| `404.html` | GitHub Pages 的自定义 404。 |
| `robots.txt` / `sitemap.xml` | 搜索引擎用。 |
| `.nojekyll` | 跳过 Jekyll；即使改回分支发布也不会被处理。 |

## 本地预览

不需要装任何东西，用任意静态服务器打开 `site/` 即可：

```bash
python -m http.server 8080 --directory site
# 然后访问 http://localhost:8080/
```

直接双击 `index.html` 也能看，只是复制按钮在 `file://` 下可能被浏览器禁用。

## 发布

Pages 设置：**Source: GitHub Actions**。工作流只上传 `site/`，不跑构建，也不需要额外的
PAT：`actions/configure-pages`、`upload-pages-artifact` 和 `deploy-pages` 都用仓库自带的
`GITHUB_TOKEN`。

站点是项目页，地址带仓库名。页面里的 `canonical`、Open Graph、`sitemap.xml`、`robots.txt`
和 404 的资源路径都已按 `/Lolitacoding.Nacos/` 写好；如果以后绑定自定义域名，这几处要一起改。

## 改内容时注意

- 页面上的版本对照表来自 `docs/faq.md` 与 `docs/releasing.md`，Nacos 支持范围变了要同步。
- 代码示例必须能编译：它们是从 README 与 `docs/quickstart.md` 抄来的，改了 API 记得一起改。
  配置 watch 的示例使用 `NacosConfigWatchEvent` 的联合类型分支。
- 数字（单元测试 926、集成测试 64）会随仓库变化，发版前顺手更新。
