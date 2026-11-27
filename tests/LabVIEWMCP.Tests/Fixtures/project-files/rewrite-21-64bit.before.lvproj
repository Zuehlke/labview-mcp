<?xml version='1.0' encoding='UTF-8'?>
<Project Type="Project" LVVersion="21008000">
	<Property Name="NI.LV.All.SaveVersion" Type="Str">21.0</Property>
	<Property Name="NI.LV.All.SourceOnly" Type="Bool">true</Property>
	<Property Name="NI.Project.Description" Type="Str"></Property>
	<Item Name="My Computer" Type="My Computer">
		<Property Name="NI.SortType" Type="Int">3</Property>
		<Property Name="server.app.propertiesEnabled" Type="Bool">true</Property>
		<Property Name="server.tcp.enabled" Type="Bool">false</Property>
		<Item Name="Helpers" Type="Folder">
			<Item Name="Scale.vi" Type="VI" URL="../Helpers/Scale.vi"/>
		</Item>
		<Item Name="Main.vi" Type="VI" URL="../Main.vi"/>
		<Item Name="Dependencies" Type="Dependencies">
			<Item Name="vi.lib" Type="Folder">
				<Item Name="Clear Errors.vi" Type="VI" URL="/&lt;vilib&gt;/Utility/error.llb/Clear Errors.vi"/>
				<Item Name="System Directory Type.ctl" Type="VI" URL="/&lt;vilib&gt;/Utility/sysdir.llb/System Directory Type.ctl"/>
				<Item Name="NI_Vision_Acquisition_Software.lvlib" Type="Library" URL="/&lt;vilib&gt;/vision/driver/NI_Vision_Acquisition_Software.lvlib"/>
			</Item>
			<Item Name="Outside.vi" Type="VI" URL="../../Outside.vi"/>
		</Item>
		<Item Name="Build Specifications" Type="Build">
			<Item Name="App" Type="EXE">
				<Property Name="App_copyErrors" Type="Bool">true</Property>
				<Property Name="Bld_buildSpecName" Type="Str">App</Property>
			</Item>
			<Item Name="App Installer" Type="Installer">
				<Property Name="DistPart[4].flavorID" Type="Str">DefaultFull</Property>
				<Property Name="DistPart[4].productID" Type="Str">{3BD81D89-DF1B-460D-8F73-9F8C29DA6771}</Property>
				<Property Name="DistPart[4].productName" Type="Str">NI LabVIEW Runtime 2021 SP1 f6 (64-bit)</Property>
				<Property Name="DistPart[4].upgradeCode" Type="Str">{130967B8-62DA-3725-A46E-2E8360EA95EA}</Property>
				<Property Name="DistPartCount" Type="Int">7</Property>
				<Property Name="InstSpecBitness" Type="Str">64-bit</Property>
				<Property Name="InstSpecVersion" Type="Str">21018002</Property>
			</Item>
		</Item>
	</Item>
</Project>
