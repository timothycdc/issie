﻿module SheetBeautify
open DrawModelType
let SheetBeautify (sheet: SheetT.Model) =
    let optimizedSheet =
        sheet
        |> SheetBeautifyFlip.optimizePortOrder
        |> SheetBeautifyAlign.sheetAlignScale 3
        |> SheetBeautifyWireLabel.wireLabelBeautify
    {optimizedSheet with UndoList = sheet::sheet.UndoList}