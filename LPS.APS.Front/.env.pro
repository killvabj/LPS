# 环境
VITE_NODE_ENV=production

# 接口前缀
VITE_API_BASE_PATH=

# APS 后端 baseURL（生产环境）
#  - 11-12 4号位 落库：此前只由 scripts/build-delivery.mjs 在构建时注入，漏注入时产物会
#    **静默**回落到 http://localhost:5163（src/api/aps-v1/http.ts:30 的 fallback）——
#    构建照样成功、包看着正常，PM 打开就是"打不通"。写进本文件＝不再依赖记不记得带参数。
#  - scripts/build-delivery.mjs 的默认基址**从本文件读**（单一真相源）；--api 仍可临时覆盖
#    （vite loadEnv 里 process.env 优先于文件值）。
#  - dev 走 .env.dev（https://localhost:7044），两者互不影响。
VITE_APS_API_BASE_URL="http://10.116.2.75:8081"

# 打包路径
VITE_BASE_PATH=/

# 是否删除debugger
VITE_DROP_DEBUGGER=true

# 是否删除console.log
VITE_DROP_CONSOLE=true

# 是否sourcemap
VITE_SOURCEMAP=false

# 输出路径
VITE_OUT_DIR=dist-pro

# 标题
VITE_APP_TITLE=APS V1

# 是否包分析
VITE_USE_BUNDLE_ANALYZER=true

# 是否全量引入element-plus样式
VITE_USE_ALL_ELEMENT_PLUS_STYLE=false

# 是否开启mock
# 10-15 4号位 置 false：线上包绝不能带 mock。true 会同时开启 viteMockServe（把 mock/ 全量塞进
# 生产包）并让 api/aps-v1/*.ts 里所有 APS_USE_MOCK 分支返回 MOCK_* 桩数据，页面上看到的数字全是假的。
# 要临时看桩数据只在本地起 dev 时开，不要改这里。
VITE_USE_MOCK=false

# 是否切割css
VITE_USE_CSS_SPLIT=true

# 是否使用在线图标
VITE_USE_ONLINE_ICON=false

# 是否隐藏全局设置按钮
VITE_HIDE_GLOBAL_SETTING=false
