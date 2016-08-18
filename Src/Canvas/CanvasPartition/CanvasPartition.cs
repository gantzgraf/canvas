﻿using System;
using System.Collections.Generic;
using System.IO;
using NDesk.Options;

namespace CanvasPartition
{
    class CanvasPartition
    {
        static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: CanvasPartition.exe [OPTIONS]+");
            Console.WriteLine("Divide bins into consistent intervals based on their counts");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }

        static int Main(string[] args)
        {
            CanvasCommon.Utilities.LogCommandLine(args);
            string inFile = null;
            string outFile = null;
            bool needHelp = false;
            bool isGermline = false;
            string bedPath = null;
            string commonCNVsbedPath = null;
            double alpha = Segmentation.DefaultAlpha;
            double madFactor = Segmentation.DefaultMadFactor;
            SegmentSplitUndo undoMethod = SegmentSplitUndo.None;
            SegmentationMethod partitionMethod = SegmentationMethod.Wavelets;
            int maxInterBinDistInSegment = 1000000;
            OptionSet p = new OptionSet()
            {
                { "i|infile=", "input file - usually generated by CanvasClean", v => inFile = v },
                { "o|outfile=", "text file to output", v => outFile = v },
                { "h|help", "show this message and exit", v => needHelp = v != null },
                { "m|method=", "segmentation method (Wavelets/CBS). Default: " + partitionMethod, v => partitionMethod = (SegmentationMethod)Enum.Parse(typeof(SegmentationMethod), v) },
                { "a|alpha=", "alpha parameter to CBS. Default: " + alpha, v => alpha = float.Parse(v) },
                { "s|split=", "CBS split method (None/Prune/SDUndo). Default: " + undoMethod, v => undoMethod = (SegmentSplitUndo)Enum.Parse(typeof(SegmentSplitUndo), v) },
                { "f|madFactor=", "MAD factor to Wavelets. Default: " + madFactor, v => madFactor = float.Parse(v) },
                { "b|bedfile=", "bed file to exclude (don't span these intervals)", v => bedPath = v },
                { "c|commoncnvs=", "bed file with common CNVs (always include these intervals into segmentation results)", v => commonCNVsbedPath = v },             
                { "g|germline", "flag indicating that input file represents germline genome", v => isGermline = v != null },
                { "d|maxInterBinDistInSegment=", "the maximum distance between adjacent bins in a segment (negative numbers turn off splitting segments after segmentation). Default: " + maxInterBinDistInSegment, v => maxInterBinDistInSegment = int.Parse(v) },
            };

            List<string> extraArgs = p.Parse(args);
            if (extraArgs.Count > 0)
            {
                Console.WriteLine("* Error: I don't understand the argument '{0}'", extraArgs[0]);
                needHelp = true;
            }

            if (needHelp)
            {
                ShowHelp(p);
                return 0;
            }

            if (inFile == null || outFile == null)
            {
                ShowHelp(p);
                return 0;
            }

            if (!File.Exists(inFile))
            {
                Console.WriteLine("CanvasPartition.exe: File {0} does not exist! Exiting.", inFile);
                return 1;
            }

            if (!string.IsNullOrEmpty(bedPath) && !File.Exists(bedPath))
            {
                Console.WriteLine("CanvasPartition.exe: File {0} does not exist! Exiting.", bedPath);
                return 1;
            }

            // no command line parameter for segmentation method
            Segmentation SegmentationEngine = new Segmentation(inFile, bedPath, maxInterBinDistInSegment: maxInterBinDistInSegment);
            SegmentationEngine.Alpha = alpha;
            SegmentationEngine.UndoMethod = undoMethod;
            SegmentationEngine.MadFactor = madFactor;
            SegmentationEngine.SegmentGenome(outFile, partitionMethod, isGermline, commonCNVsbedPath);
            return 0;
        }
    }
}
