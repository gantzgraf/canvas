﻿using System;
using Canvas.CommandLineParsing;
using CanvasCommon;
using Illumina.Common.FileSystem;
using Isas.Framework.FrameworkFactory;
using Isas.Framework.Settings;

namespace Canvas
{
    public class Program
    {
        public static int Main(string[] args)
        {
            var mainParser = new MainParser(CanvasVersionInfo.VersionString, CanvasVersionInfo.CopyrightString,
                new GermlineWgsModeParser("Germline-WGS", "CNV calling of a germline sample from whole genome sequencing data"),
                new SomaticEnrichmentModeParser("Somatic-Enrichment", "CNV calling of a somatic sample from targeted sequencing data"),
                new TumorNormalWgsModeParser("Somatic-WGS", "CNV calling of a somatic sample from whole genome sequencing data"),
                new TumorNormalEnrichmentModeParser("Tumor-normal-enrichment", "CNV calling of a tumor/normal pair from targeted sequencing data"),
                new SmallPedigreeModeParser("SmallPedigree-WGS", "CNV calling of a small pedigree from whole genome sequencing data"));
            var result = mainParser.Run(args, Console.Out, Console.Error);
            return result;
        }
    }
}
