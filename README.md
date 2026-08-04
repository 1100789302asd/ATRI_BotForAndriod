# ATRI Desktop Pet

这是一个基于 Unity 的桌面宠物/移动端交互项目，包含 Live2D 展示、Socket.IO 对话桥接、网易云音乐接口调用和基础 UI 控制。

本仓库是非官方项目，未获得 ATRI、Live2D、网易云音乐或相关素材权利方的背书。开源前请特别注意：代码可以选择开源许可，素材和角色设定必须按各自授权单独处理。

## 开源范围

- 原创源代码默认按 [MIT License](LICENSE) 发布。
- 第三方 SDK、字体、角色模型、图片、音频、视频、音乐、语音素材和角色设定不因本仓库的 MIT 许可而自动获得再分发授权。
- 已知第三方和待确认资源见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 环境

- Unity `6000.0.28f1c1`
- 依赖以 [Packages/manifest.json](Packages/manifest.json) 为准
- Live2D Cubism SDK for Unity
- Socket.IO 服务端，默认地址为 `http://127.0.0.1:5000`

## 本地配置

主要运行配置位于：

- `Assets/StreamingAssets/model/atri/config/server_config.json`
- `Assets/model/atri/config/server_config.json`

网易云登录 cookie 属于本地敏感数据，文件名为 `netease_cookie.json`。该文件和对应 `.meta` 已在 `.gitignore` 中忽略，不应提交到公开仓库。

## 开源发布前检查

1. 确认没有提交账号 cookie、token、密钥、聊天记录、真实手机号或本地绝对路径。
2. 确认 `Assets/model/atri`、`Assets/StreamingAssets/model/atri`、`Assets/Resources/bk`、`Assets/Font/STXINWEI.TTF` 等素材是否有再分发授权。
3. 如果素材授权不明确，建议改成代码开源，素材由使用者自行放入本地目录。
4. 大型二进制文件不要直接放进普通 Git 历史；需要发布时优先使用 Git LFS、Release 附件或外部下载说明。
5. 公开 README 中保留“非官方项目”和“素材不随代码授权”的说明。

更细的检查清单位于 [OPEN_SOURCE_CHECKLIST.md](OPEN_SOURCE_CHECKLIST.md)。
