# HomeButler 源码收录授权说明

本仓库在 `third-party/homebutler/` 中收录 HomeButler 2.0.0 的源码，依据如下。

## 权利链条

- 原始 BaseButler 作者：**lingxiangtingsheng**，原作页面 <https://www.nexusmods.com/survivallog/mods/4>
- 适配整合包维护者：**lingbaicat**，上游仓库 <https://github.com/lingbaicat/SurvivalLog-HomeButler-lingbaicat>
- 上游包的 `manifest.json` 记录 `Authorization: "Maintainer confirms permission obtained for this release"`，即维护者已就其发布取得原作者许可。
- 本仓库收录 HomeButler 源码：由仓库所有者 **KaiXuan666** 向权利人取得许可后加入，确认时间为 2026-10-20。

## 收录范围

| 内容 | 状态 |
| --- | --- |
| `source/` 源码（`.cs`、`.js`、`.csproj`、测试与校验脚本） | 已收录，用于查阅与维护 |
| 上游 README、CHANGELOG | 已收录，保留原始说明 |
| 原始许可文本 | 已收录，见 `HomeButler-LICENSE.md` |
| 编译产物 `BaseButler*.dll` | 未收录 |
| 安装器、运行时与打包清单（`Setup.ps1`、`Launch.ps1`、`payload/`、`manifest.json`、`validation/`） | 未收录 |
| 游戏代码、素材、商标及生成的游戏程序集 | 未收录，且本项目不授予其使用权 |

## 未授予的权利

收集源码 **不等于** 重新许可。本仓库未获得、也未向任何下游授予以下权利：

- 将 HomeButler 重新许可为 MIT、GPL 或其他开源许可；
- 商业使用；
- 超出查阅与维护目的的分发、改编或其他使用。

需要超出上述范围使用时，请联系权利人确认。授权沟通记录由仓库所有者留存，不随仓库公开——处理方式与上游包一致（公开包不包含私人聊天记录）。

## 撤回

如权利人要求调整收录范围，直接移除 `third-party/homebutler/` 目录即可，不影响仓库其余部分。
