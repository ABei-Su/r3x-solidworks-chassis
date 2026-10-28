# R3X SolidWorks Chassis

R3X 320 × 240 mm 小车底盘的 SolidWorks 零件模型，以及 Windows / SolidWorks COM 连接与建模工具源码。

## 下载模型

[下载 SolidWorks 零件模型](models/R3X_Chassis_320x240x3mm.SLDPRT)

- 根据 WHEELTEC R3X 加大版孔位图重建的工作模型，非厂商官方模型。
- 外形尺寸：320 × 240 mm。
- 板厚：暂按 3 mm 建模；源孔位图未标注厚度。
- 当前阶段：外轮廓与圆孔切除；此前建模记录为 189 个圆孔、清理残片后一个实体。
- 未完成：非圆槽孔；尚未完成制造尺寸、公差和装配适配性复核。
- 文件由 SolidWorks 2022 SP5 生成。本次发布检查文件完整性与源码编译，不代表重新完成 CAD 几何验收。

## 项目文件

```text
models/                          SolidWorks 原生模型及 SHA-256 校验记录
SolidWorksBridge/Program.cs       现有连接器源码
SolidWorksBridge/build.ps1        本机编译脚本
SolidWorksBridge/SolidWorksBridge.cmd
CHANGELOG.md                     发布记录
```

原始厂商 DWG/PDF、临时失败模型、个人迁移记录和 SolidWorks 官方 DLL 未包含在仓库中。原始设计权益仍属于各自权利人；本次未为其添加新的授权许可证。

## 编译连接器

需要 Windows、.NET Framework C# 编译器，以及本机安装的 SolidWorks Interop 程序集。将 `-InteropDirectory` 指向自己的 SolidWorks `api/redist` 目录：

```powershell
& .\SolidWorksBridge\build.ps1 -InteropDirectory 'F:\Program Files\SOLIDWORKS Corp2022\SOLIDWORKS\api\redist'
```

脚本将 EXE 和所需的两个 Interop DLL 放入本地 `SolidWorksBridge/bin`，该目录不提交到 Git。

## 连接与检查

先打开 SolidWorks，再运行：

```powershell
.\SolidWorksBridge\SolidWorksBridge.cmd status
.\SolidWorksBridge\SolidWorksBridge.cmd verify-active
```

连接器附着活动 SolidWorks 会话；没有活动实例时会尝试启动一个。诊断输出可能包含活动文件路径。

## 建模命令与限制

以下为历史建模时使用的分步命令，不是保证完整重建的单命令流水线。涉及修改活动文档的命令应在另存的工作副本上运行。

```powershell
.\SolidWorksBridge\SolidWorksBridge.cmd build-chassis '输入.dwg' '工作副本.sldprt'
.\SolidWorksBridge\SolidWorksBridge.cmd cut-circle-batches
.\SolidWorksBridge\SolidWorksBridge.cmd verify-active
```

`import-inspect` 在现有代码中也会执行建模和保存，不能视为只读命令。部分几何操作失败时程序仍可能返回退出码 0，必须检查 `EXTRUDE`、`CIRCLE_CUT`、`SKIPPED_CIRCLES`、实体数量和保存结果。

`cleanup-small-bodies` 实际会删除除最大实体之外的所有实体，并非按体积阈值过滤。仅在检查过每个实体、确认其余实体确为残片后使用。

## 校验模型

`models/checksums.json` 记录发布模型文件的 SHA-256。可使用 PowerShell `Get-FileHash -Algorithm SHA256` 检查下载文件。
