namespace LabEquipmentController
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        // Layout grid (design units @ 96 DPI):
        //   page margin 12 | groups 1476 wide (12..1488) | client 1500x980
        //   inputs (combo/text/numeric) are 23 tall, buttons 26 tall
        //   a label sits 4px below its input's top so their text baselines line up
        //
        // The group width has to track the client width. Anchoring preserves the margin a
        // control was designed with, so leaving the groups at their old 916 inside a wider
        // client does not stretch them — it bakes the difference in as dead space down the
        // right-hand side. Same for anything anchored Right inside a group: widen the group
        // and its right-anchored children have to move with it.

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.grpScan = new System.Windows.Forms.GroupBox();
            this.lblInterface = new System.Windows.Forms.Label();
            this.cboInterface = new System.Windows.Forms.ComboBox();
            this.lblPort = new System.Windows.Forms.Label();
            this.cboPort = new System.Windows.Forms.ComboBox();
            this.lblRange = new System.Windows.Forms.Label();
            this.txtRange = new System.Windows.Forms.TextBox();
            this.lblTimeout = new System.Windows.Forms.Label();
            this.numTimeout = new System.Windows.Forms.NumericUpDown();
            this.btnScan = new System.Windows.Forms.Button();
            this.progressScan = new System.Windows.Forms.ProgressBar();
            this.lblStatus = new System.Windows.Forms.Label();
            this.btnExport = new System.Windows.Forms.Button();
            this.grpDevices = new System.Windows.Forms.GroupBox();
            this.lstDevices = new System.Windows.Forms.ListView();
            this.colIp = new System.Windows.Forms.ColumnHeader();
            this.colPort = new System.Windows.Forms.ColumnHeader();
            this.colProto = new System.Windows.Forms.ColumnHeader();
            this.colIdentity = new System.Windows.Forms.ColumnHeader();
            this.lblAddress = new System.Windows.Forms.Label();
            this.txtAddress = new System.Windows.Forms.TextBox();
            this.btnConnect = new System.Windows.Forms.Button();
            this.btnSequence = new System.Windows.Forms.Button();
            this.lblConnection = new System.Windows.Forms.Label();
            this.grpConsole = new System.Windows.Forms.GroupBox();
            this.tabConsoles = new System.Windows.Forms.TabControl();
            this.lblNoConsole = new System.Windows.Forms.Label();
            this.grpScan.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numTimeout)).BeginInit();
            this.grpDevices.SuspendLayout();
            this.grpConsole.SuspendLayout();
            this.SuspendLayout();
            //
            // grpScan
            //
            this.grpScan.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.grpScan.Controls.Add(this.lblInterface);
            this.grpScan.Controls.Add(this.cboInterface);
            this.grpScan.Controls.Add(this.lblPort);
            this.grpScan.Controls.Add(this.cboPort);
            this.grpScan.Controls.Add(this.lblRange);
            this.grpScan.Controls.Add(this.txtRange);
            this.grpScan.Controls.Add(this.btnScan);
            this.grpScan.Controls.Add(this.progressScan);
            this.grpScan.Controls.Add(this.lblStatus);
            this.grpScan.Location = new System.Drawing.Point(12, 12);
            this.grpScan.Name = "grpScan";
            this.grpScan.Size = new System.Drawing.Size(1476, 90);
            this.grpScan.TabIndex = 0;
            this.grpScan.TabStop = false;
            this.grpScan.Text = "Network Scan";
            //
            // lblInterface
            //
            this.lblInterface.AutoSize = true;
            this.lblInterface.Location = new System.Drawing.Point(12, 28);
            this.lblInterface.Name = "lblInterface";
            this.lblInterface.TabIndex = 0;
            this.lblInterface.Text = "Interface:";
            //
            // cboInterface
            //
            this.cboInterface.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cboInterface.Location = new System.Drawing.Point(78, 24);
            this.cboInterface.Name = "cboInterface";
            this.cboInterface.Size = new System.Drawing.Size(330, 23);
            this.cboInterface.TabIndex = 1;
            //
            // lblPort
            //
            this.lblPort.AutoSize = true;
            this.lblPort.Location = new System.Drawing.Point(424, 28);
            this.lblPort.Name = "lblPort";
            this.lblPort.TabIndex = 2;
            this.lblPort.Text = "SCPI Port(s):";
            //
            // cboPort
            //
            this.cboPort.FormattingEnabled = true;
            // 111 = VXI-11 (portmapper). Any other port is probed as a raw socket.
            this.cboPort.Items.AddRange(new object[] { "5025", "5555", "3490", "111", "5025, 5555", "5025, 5555, 111", "5025, 5555, 3490, 111" });
            this.cboPort.Location = new System.Drawing.Point(508, 24);
            this.cboPort.Name = "cboPort";
            this.cboPort.Size = new System.Drawing.Size(130, 23);
            this.cboPort.TabIndex = 3;
            this.cboPort.Text = "5025, 5555, 3490, 111";
            //
            // btnScan
            //
            // Doubles as Stop: the text flips to "Stop" while a scan is running.
            // Height matches the 23px inputs on this row.
            // Sits just after the port list rather than out at the right margin: it acts on
            // those inputs, and keeping it close is what lets the window narrow this far.
            this.btnScan.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)));
            this.btnScan.Location = new System.Drawing.Point(654, 24);
            this.btnScan.Name = "btnScan";
            this.btnScan.Size = new System.Drawing.Size(80, 23);
            this.btnScan.TabIndex = 6;
            this.btnScan.Text = "Scan";
            this.btnScan.UseVisualStyleBackColor = true;
            this.btnScan.Click += new System.EventHandler(this.btnScan_Click);
            //
            // progressScan
            //
            // Leads the status line: the bar and the text it describes read as one unit,
            // so a fixed width anchored left — it must not stretch away from its label.
            this.progressScan.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)));
            this.progressScan.Location = new System.Drawing.Point(12, 57);
            this.progressScan.Name = "progressScan";
            this.progressScan.Size = new System.Drawing.Size(154, 18);
            this.progressScan.TabIndex = 7;
            //
            // lblStatus
            //
            // On its own row: the status text is long ("Scanning 254 hosts on port(s)
            // 5025, 5555, 111...") and was previously clipped by the Stop button.
            this.lblStatus.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.lblStatus.AutoEllipsis = true;
            // Starts after the progress bar that leads this row.
            this.lblStatus.Location = new System.Drawing.Point(178, 58);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(966, 17);
            this.lblStatus.TabIndex = 8;
            this.lblStatus.Text = "Ready.";
            //
            // btnExport
            //
            // Immediately after Scan — it exports what the scan found. Anchored like Scan, so
            // the pair stays together. PositionExportRow owns its exact bounds. Disabled
            // until a scan yields results.
            this.btnExport.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)));
            this.btnExport.Enabled = false;
            this.btnExport.Location = new System.Drawing.Point(766, 26);
            this.btnExport.Name = "btnExport";
            this.btnExport.Size = new System.Drawing.Size(130, 20);
            this.btnExport.TabIndex = 9;
            this.btnExport.Text = "Export Results…";
            this.btnExport.UseVisualStyleBackColor = true;
            this.btnExport.Click += new System.EventHandler(this.btnExport_Click);
            //
            // lblRange, txtRange
            //
            // Narrows the sweep to part of the subnet. Empty means the whole thing, which is
            // what it did before this existed, so the default behaviour is unchanged.
            // PositionScanRangeField owns their bounds — they take whatever the row has left,
            // which grows with the window.
            this.lblRange.AutoSize = true;
            this.lblRange.Location = new System.Drawing.Point(908, 28);
            this.lblRange.Name = "lblRange";
            this.lblRange.TabIndex = 10;
            this.lblRange.Text = "IP range:";
            //
            this.txtRange.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtRange.Location = new System.Drawing.Point(972, 24);
            this.txtRange.Name = "txtRange";
            this.txtRange.PlaceholderText = "whole subnet";
            this.txtRange.Size = new System.Drawing.Size(172, 23);
            this.txtRange.TabIndex = 11;
            //
            // grpDevices
            //
            // Height is owned by MainForm.LayoutGroups (it guarantees a readable number of
            // rows), so this group is NOT bottom-anchored — the consoles below take the
            // slack instead.
            this.grpDevices.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.grpDevices.Controls.Add(this.btnExport);
            this.grpDevices.Controls.Add(this.lstDevices);
            this.grpDevices.Controls.Add(this.lblAddress);
            this.grpDevices.Controls.Add(this.txtAddress);
            this.grpDevices.Controls.Add(this.btnConnect);
            this.grpDevices.Controls.Add(this.lblConnection);
            this.grpDevices.Controls.Add(this.lblTimeout);
            this.grpDevices.Controls.Add(this.numTimeout);
            this.grpDevices.Location = new System.Drawing.Point(12, 108);
            this.grpDevices.Name = "grpDevices";
            this.grpDevices.Size = new System.Drawing.Size(1476, 200);
            this.grpDevices.TabIndex = 1;
            this.grpDevices.TabStop = false;
            this.grpDevices.Text = "Discovered Instruments  (double-click a row to connect)";
            //
            // lstDevices
            //
            this.lstDevices.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.lstDevices.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
                this.colIp,
                this.colPort,
                this.colProto,
                this.colIdentity});
            this.lstDevices.FullRowSelect = true;
            this.lstDevices.GridLines = true;
            this.lstDevices.Location = new System.Drawing.Point(12, 24);
            this.lstDevices.MultiSelect = false;
            this.lstDevices.Name = "lstDevices";
            // 1452 = the group's 1476 less the same 12 either side, so the right inset matches
            // the left one and the anchor keeps them equal at every window width. It was 1132,
            // leaving 332 units spare on the right — the room Export Results used to occupy up
            // here, never reclaimed when that button moved down to the connect row. Anchored
            // Right, the list carried that stale gap into every size: 618px of empty grid at
            // 2714 wide. Export is placed from lstDevices.Right (see PositionTimeoutField), so
            // it follows this rather than needing its own correction.
            this.lstDevices.Size = new System.Drawing.Size(1452, 130);
            this.lstDevices.TabIndex = 0;
            this.lstDevices.UseCompatibleStateImageBehavior = false;
            this.lstDevices.View = System.Windows.Forms.View.Details;
            this.lstDevices.SelectedIndexChanged += new System.EventHandler(this.lstDevices_SelectedIndexChanged);
            this.lstDevices.DoubleClick += new System.EventHandler(this.lstDevices_DoubleClick);
            //
            // colIp
            //
            this.colIp.Text = "IP Address";
            this.colIp.Width = 120;
            //
            // colPort
            //
            this.colPort.Text = "Port";
            this.colPort.Width = 55;
            //
            // colProto
            //
            this.colProto.Text = "Protocol";
            this.colProto.Width = 90;
            //
            // colIdentity
            //
            // Sized so the four columns fill the list's width — no dead strip on the right.
            // A design-time value only: LayoutDeviceColumns recomputes it against the real
            // client width, because column widths do not go through the auto-scaling pass.
            this.colIdentity.Text = "Identity (*IDN?)";
            this.colIdentity.Width = 1166;
            //
            // lblAddress
            //
            this.lblAddress.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.lblAddress.AutoSize = true;
            this.lblAddress.Location = new System.Drawing.Point(12, 170);
            this.lblAddress.Name = "lblAddress";
            this.lblAddress.TabIndex = 1;
            this.lblAddress.Text = "Address:";
            //
            // txtAddress
            //
            this.txtAddress.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.txtAddress.Location = new System.Drawing.Point(74, 166);
            this.txtAddress.Name = "txtAddress";
            this.txtAddress.PlaceholderText = "192.168.1.50   or   192.168.1.50:5555";
            this.txtAddress.Size = new System.Drawing.Size(200, 23);
            this.txtAddress.TabIndex = 2;
            //
            // btnConnect
            //
            // Doubles as Disconnect: the text flips once a link is open.
            // Height matches the 23px Address box beside it.
            this.btnConnect.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnConnect.Location = new System.Drawing.Point(284, 166);
            this.btnConnect.Name = "btnConnect";
            this.btnConnect.Size = new System.Drawing.Size(95, 23);
            this.btnConnect.TabIndex = 3;
            this.btnConnect.Text = "Connect";
            this.btnConnect.UseVisualStyleBackColor = true;
            this.btnConnect.Click += new System.EventHandler(this.btnConnect_Click);
            //
            // lblConnection
            //
            this.lblConnection.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.lblConnection.AutoSize = true;
            this.lblConnection.Location = new System.Drawing.Point(392, 170);
            this.lblConnection.Name = "lblConnection";
            this.lblConnection.TabIndex = 4;
            this.lblConnection.Text = "Not connected.";
            //
            // btnSequence
            //
            // Also on Tools, but a multi-instrument script is something you reach for
            // repeatedly while working — a menu is the wrong place for that. It sits at the
            // right-hand end of the console tab strip, because that strip is the list of
            // instruments a script will address, and PositionSequenceButton keeps it level
            // with the tabs.
            this.btnSequence.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            // 1476 group - 150 wide - 4 gap - 2 border. The Right anchor holds whatever margin
            // this coordinate implies, so it has to track the group width like everything else
            // anchored Right in here; PositionSequenceButton then keeps it there at runtime.
            this.btnSequence.Location = new System.Drawing.Point(1320, 18);
            this.btnSequence.Name = "btnSequence";
            this.btnSequence.Size = new System.Drawing.Size(150, 23);
            this.btnSequence.TabIndex = 5;
            this.btnSequence.Text = "Multi-Instrument Scripts…";
            this.btnSequence.UseVisualStyleBackColor = true;
            this.btnSequence.Click += new System.EventHandler(this.btnSequence_Click);
            //
            // lblTimeout
            //
            // Sits with Connect (not the scan row): it's the instrument communication
            // timeout, used when connecting and querying — the scan has its own fixed,
            // short probe timeouts so a large value here never slows discovery.
            this.lblTimeout.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.lblTimeout.AutoSize = true;
            this.lblTimeout.Location = new System.Drawing.Point(980, 170);
            this.lblTimeout.Name = "lblTimeout";
            this.lblTimeout.TabIndex = 5;
            this.lblTimeout.Text = "Timeout (ms):";
            //
            // numTimeout
            //
            this.numTimeout.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.numTimeout.Increment = new decimal(new int[] { 500, 0, 0, 0 });
            this.numTimeout.Location = new System.Drawing.Point(1062, 166);
            this.numTimeout.Maximum = new decimal(new int[] { 30000, 0, 0, 0 });
            this.numTimeout.Minimum = new decimal(new int[] { 100, 0, 0, 0 });
            this.numTimeout.Name = "numTimeout";
            this.numTimeout.Size = new System.Drawing.Size(70, 23);
            this.numTimeout.TabIndex = 6;
            this.numTimeout.TextAlign = System.Windows.Forms.HorizontalAlignment.Right;
            this.numTimeout.Value = new decimal(new int[] { 3000, 0, 0, 0 });
            //
            // grpConsole
            //
            // Holds one console per connected instrument. The consoles themselves are
            // InstrumentConsole user controls built in code — there is one per session, and
            // one can be lifted out into its own window, so they can't be designer controls.
            // Anchored on all four edges: LayoutGroups sets its Top once, and the group
            // then stretches to fill whatever height the window has left.
            this.grpConsole.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                | System.Windows.Forms.AnchorStyles.Left)
                | System.Windows.Forms.AnchorStyles.Right)));
            this.grpConsole.Controls.Add(this.btnSequence);
            this.grpConsole.Controls.Add(this.tabConsoles);
            this.grpConsole.Controls.Add(this.lblNoConsole);
            this.grpConsole.Location = new System.Drawing.Point(12, 314);
            this.grpConsole.Name = "grpConsole";
            this.grpConsole.Padding = new System.Windows.Forms.Padding(8, 6, 8, 8);
            // Reaches the bottom of ClientSize less the same 12px margin the other edges use:
            // 314 + 654 = 968, and 980 - 968 = 12. MainForm measures that gap once at startup
            // and preserves it at every window size, so a console box left behind when
            // ClientSize grows becomes a gap under the console for ever.
            this.grpConsole.Size = new System.Drawing.Size(1476, 654);
            this.grpConsole.TabIndex = 2;
            this.grpConsole.TabStop = false;
            this.grpConsole.Text = "Instrument Consoles";
            //
            // tabConsoles
            //
            // One tab per open connection; pages are added by ConnectSelectedAsync and
            // removed when a session is closed or detached into its own window.
            this.tabConsoles.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabConsoles.Name = "tabConsoles";
            this.tabConsoles.ShowToolTips = true;   // each page tips with its instrument's identity
            this.tabConsoles.TabIndex = 0;
            this.tabConsoles.Visible = false;
            //
            // lblNoConsole
            //
            // Shown in place of the (blank) tab strip while nothing is connected — an empty
            // panel with no explanation reads as a broken window.
            // Stated rather than left to the default, because this label is docked and the
            // two settings contradict each other if AutoSize is ever switched on.
            this.lblNoConsole.AutoSize = false;
            this.lblNoConsole.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblNoConsole.Name = "lblNoConsole";
            this.lblNoConsole.TabIndex = 1;
            this.lblNoConsole.Text = "No instrument connected.\r\n\r\nScan the network above, then double-click an instrument"
                + " (or type its address and press Connect) to open a console for it.\r\n"
                + "Each instrument gets its own tab, and any tab can be detached into its own window.";
            this.lblNoConsole.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            //
            // MainForm
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            // Wide enough that the scan row and the connect row both have slack — the
            // Identity column carries whole *IDN? strings, and at the old 940 the Timeout
            // box sat almost against the connection label. Tall enough that a console is
            // usable the moment it opens: the quick-command strip wraps to three or four
            // rows on a meter, and at the old 626 that left the log a few lines high.
            //
            // The width is set by the console's tool row, measured: its six buttons come to
            // 1361px laid end to end, the console page runs about 85px narrower than the
            // client area, so below ~1460 the row wraps to a second line and takes 54px off
            // the log and the plot beneath it. At the old 1180 it always wrapped.
            //
            // ApplyWindowSettings clamps this to the working area, so a 1366x768 laptop
            // gets as much of it as fits rather than a window taller than the screen.
            this.ClientSize = new System.Drawing.Size(1500, 980);
            this.Controls.Add(this.grpScan);
            this.Controls.Add(this.grpDevices);
            this.Controls.Add(this.grpConsole);
            // Narrower than the design width, which the layout now allows: the Scan button
            // no longer sits out at the right margin. The floor is set by the Connect row —
            // below this the right-anchored Timeout box runs into the connection label.
            this.MinimumSize = new System.Drawing.Size(820, 660);
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Lab Equipment Controller";
            this.Load += new System.EventHandler(this.MainForm_Load);
            // Sessions are torn down in FormClosing (before the window and any detached
            // console windows go away); settings are written in FormClosed.
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.MainForm_FormClosing);
            this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.MainForm_FormClosed);
            this.grpScan.ResumeLayout(false);
            this.grpScan.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.numTimeout)).EndInit();
            this.grpDevices.ResumeLayout(false);
            this.grpDevices.PerformLayout();
            // PerformLayout matters here as much as it does for the two groups above, and
            // was missing: ResumeLayout(false) resumes layout without running one, so the
            // Dock.Fill on lblNoConsole and tabConsoles was never applied and the label kept
            // its default 100x23 at (0,0) — sitting on the group's caption, which read
            // "No instrument      oles" until something else forced a re-layout. Connecting
            // did, via the resize, which is why only the empty state ever looked wrong.
            this.grpConsole.ResumeLayout(false);
            this.grpConsole.PerformLayout();
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.GroupBox grpScan;
        private System.Windows.Forms.Label lblInterface;
        private System.Windows.Forms.ComboBox cboInterface;
        private System.Windows.Forms.Label lblPort;
        private System.Windows.Forms.ComboBox cboPort;
        private System.Windows.Forms.Label lblRange;
        private System.Windows.Forms.TextBox txtRange;
        private System.Windows.Forms.Label lblTimeout;
        private System.Windows.Forms.NumericUpDown numTimeout;
        private System.Windows.Forms.Button btnScan;
        private System.Windows.Forms.Button btnExport;
        private System.Windows.Forms.ProgressBar progressScan;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.GroupBox grpDevices;
        private System.Windows.Forms.ListView lstDevices;
        private System.Windows.Forms.ColumnHeader colIp;
        private System.Windows.Forms.ColumnHeader colPort;
        private System.Windows.Forms.ColumnHeader colProto;
        private System.Windows.Forms.ColumnHeader colIdentity;
        private System.Windows.Forms.Label lblAddress;
        private System.Windows.Forms.TextBox txtAddress;
        private System.Windows.Forms.Button btnConnect;
        private System.Windows.Forms.Button btnSequence;
        private System.Windows.Forms.Label lblConnection;
        private System.Windows.Forms.GroupBox grpConsole;
        private System.Windows.Forms.TabControl tabConsoles;
        private System.Windows.Forms.Label lblNoConsole;
    }
}
